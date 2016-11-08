using OTEX.Packets;
        /// Lock object for operations and clients state;
        private readonly object stateLock = new object();
        /// <param name="guid">Session ID for this server. Leaving it null will auto-generate one.</param>
        /// <exception cref="ArgumentOutOfRangeException" />
        public Server(Guid? guid = null) : base(guid)
                                masterOperations.Add(new Operation(ID, 0, fileContents = File.ReadAllText(FilePath, FilePath.DetectEncoding())));
                        thread = new Thread(ListenThread);
                        thread.IsBackground = false;
                        thread.Start( listener6 );

                        //fire event
                        OnStarted?.Invoke(this);
                    }
                }
            }
        }

        /////////////////////////////////////////////////////////////////////
        // MAIN LISTENING THREAD
        /////////////////////////////////////////////////////////////////////

        private void ListenThread(object listenerObject)
        {
            TcpListener listener = listenerObject as TcpListener;
            int flushTimeout = 60000;
            List<Thread> clientThreads = new List<Thread>();
            while (running)
            {
                //sleep a bit (not raw spin-waiting)
                Thread.Sleep(10);

                //accept the connection
                while (listener.Pending())
                {
                    //get client
                    TcpClient tcpClient = null;
                    if (CaptureException(() => { tcpClient = listener.AcceptTcpClient(); }))
                        continue;

                    //create client thread
                    clientThreads.Add(new Thread(ClientThread));
                    clientThreads.Last().IsBackground = false;
                    clientThreads.Last().Start(tcpClient);
                }

                //flush file contents to disk periodically
                if ((flushTimeout -= 10) <= 0)
                {
                    flushTimeout = 60000;
                    lock (stateLock)
                    {
                        CaptureException(() => { SyncFileContents(); });
                    }
                }
            }

            //stop tcp listener
            CaptureException(() => { listener.Stop(); });

            //wait for client threads to close
            foreach (Thread thread in clientThreads)
                thread.Join();

            //final flush to disk (don't need a lock this time, all client threads have stopped)
            CaptureException(() => { SyncFileContents(); });
        }

        /////////////////////////////////////////////////////////////////////
        // PER-CLIENT THREAD
        /////////////////////////////////////////////////////////////////////

        private void ClientThread(object tcpClientObject)
        {
            TcpClient client = tcpClientObject as TcpClient;

            //create packet stream
            PacketStream stream = null;
            if (CaptureException(() => { stream = new PacketStream(client); }))
                return;

            //read from stream
            Guid clientGUID = Guid.Empty;
            bool clientSideDisconnect = false;
            while (running && stream.Connected)
            {
                //check if client has sent data
                if (!stream.DataAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }

                //read incoming packet
                Packet packet = null;
                if (CaptureException(() => { packet = stream.Read(); }))
                    break;

                //check if guid matches server's (shouldn't happen?)
                if (packet.SenderID.Equals(ID))
                    continue; //ignore 

                //is this the first packet from a new client?
                if (clientGUID.Equals(Guid.Empty))
                {
                    //check if packet is a request type
                    if (packet.PayloadType != ConnectionRequest.PayloadType)
                        continue; //ignore

                    //deserialize packet
                    ConnectionRequest request = null;
                    if (CaptureException(() => { request = packet.Payload.Deserialize<ConnectionRequest>(); }))
                        break;

                    //check password
                    if ((password != null && (request.Password == null || !password.Matches(password))) //requires password
                        || (password == null && request.Password != null)) //no password required (reject incoming requests with passwords)
                    {
                        CaptureException(() =>
                            stream.Write(ID,
                                new ConnectionResponse(ConnectionResponse.ResponseCode.IncorrectPassword));
                        });
                        break;
                    }

                    lock (stateLock)
                    {
                        //duplicate id (already connected)
                        if (connectedClients.Contains(packet.SenderID))
                        {
                            CaptureException(() =>
                                stream.Write(ID,
                                    new ConnectionResponse(ConnectionResponse.ResponseCode.DuplicateGUID));
                            });
                            break;
                        }
                        //send response with initial sync list
                        if (!CaptureException(() => { stream.Write(ID, new ConnectionResponse(filePath, masterOperations)); }))
                        {
                            //create the list of staged operations for this client
                            outgoingOperations[clientGUID] = new List<Operation>();
                            //add to list of connected clients
                            connectedClients.Add(clientGUID = packet.SenderID);
                        }
                        else break;
                    }

                    //notify
                    OnClientConnected?.Invoke(this, clientGUID);
                }
                else //initial handshake sync has been performed, handle normal requests
                {
                    //check guid
                    if (!packet.SenderID.Equals(clientGUID))
                        continue; //ignore (shouldn't happen?)

                    switch (packet.PayloadType)
                    {
                        case DisconnectionRequest.PayloadType: //disconnection request from client
                            clientSideDisconnect = true;
                            break;

                        case OperationList.PayloadType: //normal update request
                            {
                                //deserialize operation request
                                OperationList incoming = null;
                                if (CaptureException(() => { incoming = packet.Payload.Deserialize<OperationList>(); }))
                                    break;
                                //lock operation lists (3a)
                                lock (stateLock)
                                    //get the list of staged operations for the sender
                                    List<Operation> outgoing = outgoingOperations[clientGUID];

                                    //if this oplist is not an empty request
                                    if (incoming.Operations != null && incoming.Operations.Count > 0)
                                        //transform incoming ops against outgoing ops (3b)
                                        if (outgoing.Count > 0)
                                            Operation.SymmetricLinearTransform(incoming.Operations, outgoing);

                                        //append incoming ops to master and to all other outgoing (3c)
                                        masterOperations.AddRange(incoming.Operations);
                                        foreach (var kvp in outgoingOperations)
                                            if (!kvp.Key.Equals(clientGUID))
                                                kvp.Value.AddRange(incoming.Operations);
                                    //send response
                                    CaptureException(() => { stream.Write(ID, new OperationList(outgoing.Count > 0 ? outgoing : null)); });
                                    //clear outgoing packet list (3d)
                                    outgoing.Clear();
                                }
                            }
                            break;
                    }
                    if (clientSideDisconnect)
                        break;
                }
            }
            //remove this client's outgoing operation set and connected clients set
            if (!clientGUID.Equals(Guid.Empty))
            {
                bool disconnected = false;
                lock (stateLock)
                {
                    outgoingOperations.Remove(clientGUID);
                    disconnected = connectedClients.Remove(clientGUID);
                }
                if (disconnected)
                {
                    //if the client has not requested a disconnection themselves, send them one
                    if (!clientSideDisconnect)
                        CaptureException(() => { stream.Write(ID, new DisconnectionRequest()); });
                    OnClientDisconnected?.Invoke(this, clientGUID);

            //close stream and tcp client
            stream.Dispose();
            client.Close();