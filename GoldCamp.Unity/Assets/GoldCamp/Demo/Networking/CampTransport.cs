using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Managing.Server;
using FishNet.Managing.Transporting;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Public so FishNet's code generator can generate the broadcast serializer.
    public struct CampEnvelope : IBroadcast
    {
        public string Payload;
    }

    public struct CampVoiceEnvelope : IBroadcast
    {
        public int Stream, Sequence;
        public string Speaker;
        public byte[] Data;
    }

    [DisallowMultipleComponent]
    public sealed class CampTransport : MonoBehaviour
    {
        public const int MaximumPayloadBytes = 128 * 1024;
        public const ushort TargetFrameRate = 60;
        private const int MaximumPeers = 4;
        private static readonly Encoding PayloadEncoding = new UTF8Encoding(false, true);

        public event Action<int, string> ServerMessage;
        public event Action<string> ClientMessage;
        public event Action<int> PeerDisconnected;
        public event Action ClientReady;
        public event Action ClientStopped;
        public event Action<int, CampVoiceFrame> ServerVoice;
        public event Action<CampVoiceFrame> ClientVoice;

        private readonly List<int> _peers = new List<int>(MaximumPeers);
        private ReadOnlyCollection<int> _peerView;
        private GameObject _ownedRoot;
        private NetworkManager _manager;
        private SinglePrefabObjects _emptyPrefabs;
        private bool _clientReady;
        private bool _startHostClient;
        private bool _subscribed;
        private ushort _port;

        public bool IsServerRunning => _manager != null && _manager.ServerManager.Started;
        public bool IsClientConnected => _clientReady && _manager != null &&
            _manager.ClientManager.Started && _manager.ClientManager.Connection.IsAuthenticated;
        public IReadOnlyCollection<int> ConnectedPeerIds => _peerView ??= _peers.AsReadOnly();

        // A true return means the connection request started; ClientReady confirms authentication.
        public bool StartHost(ushort port, bool loopbackOnly)
        {
            if (!CanStart(port) || !CreateManager(port, loopbackOnly))
                return false;

            _startHostClient = true;
            if (_manager.ServerManager.StartConnection())
                return true;

            Stop();
            return false;
        }

        public bool StartClient(string address, ushort port)
        {
            if (string.IsNullOrWhiteSpace(address) || !CanStart(port) || !CreateManager(port, true))
                return false;

            if (_manager.ClientManager.StartConnection(address.Trim(), port))
                return true;

            Stop();
            return false;
        }

        public bool SendToHost(string payload)
        {
            if (!IsClientConnected || !IsValidPayload(payload))
                return false;

            _manager.ClientManager.Broadcast(new CampEnvelope { Payload = payload }, Channel.Reliable);
            return true;
        }

        public bool SendToClient(int peer, string payload)
        {
            if (!IsServerRunning || !IsValidPayload(payload) || !_peers.Contains(peer) ||
                !_manager.ServerManager.Clients.TryGetValue(peer, out NetworkConnection connection) ||
                !connection.IsActive || !connection.IsAuthenticated)
                return false;

            _manager.ServerManager.Broadcast(connection, new CampEnvelope { Payload = payload },
                requireAuthenticated: true, channel: Channel.Reliable);
            return true;
        }

        public bool SendVoiceToHost(CampVoiceFrame frame)
        {
            if (!IsClientConnected || !ValidVoice(frame)) return false;
            _manager.ClientManager.Broadcast(new CampVoiceEnvelope { Stream = frame.Stream,
                Sequence = frame.Sequence, Speaker = "", Data = frame.Data }, Channel.Unreliable);
            return true;
        }
        public bool SendVoiceToClient(int peer, CampVoiceFrame frame)
        {
            if (!IsServerRunning || !ValidVoice(frame) || !_peers.Contains(peer) ||
                !_manager.ServerManager.Clients.TryGetValue(peer, out NetworkConnection connection) ||
                !connection.IsActive || !connection.IsAuthenticated) return false;
            _manager.ServerManager.Broadcast(connection, new CampVoiceEnvelope { Stream = frame.Stream,
                Sequence = frame.Sequence, Speaker = frame.Speaker, Data = frame.Data },
                requireAuthenticated: true, channel: Channel.Unreliable);
            return true;
        }
        private static bool ValidVoice(CampVoiceFrame frame) => frame != null && frame.Stream > 0 &&
            frame.Sequence >= 0 && frame.Data != null && frame.Data.Length > 0 &&
            frame.Data.Length <= CampVoiceCodec.MaximumPacketBytes;

        // The caller owns deliberate shutdown handling; no gameplay callbacks are raised by Stop.
        public void Stop()
        {
            _startHostClient = false;
            _clientReady = false;
            _peers.Clear();
            NetworkManager manager = _manager;
            GameObject root = _ownedRoot;
            SinglePrefabObjects prefabs = _emptyPrefabs;
            Unsubscribe();
            _manager = null;
            _ownedRoot = null;
            _emptyPrefabs = null;

            if (manager != null && manager.Initialized)
            {
                manager.ClientManager.StopConnection();
                manager.ServerManager.StopConnection(sendDisconnectMessage: true);
            }
            if (root != null)
            {
                root.SetActive(false);
                Destroy(root);
            }
            if (prefabs != null)
                Destroy(prefabs);
        }

        private bool CanStart(ushort port)
        {
            // Repeated start requests must not tear down an existing connection.
            return Application.isPlaying && isActiveAndEnabled && port != 0 && _ownedRoot == null;
        }

        private bool CreateManager(ushort port, bool loopbackOnly)
        {
            _port = port;
            _ownedRoot = new GameObject("Camp transport");
            _ownedRoot.SetActive(false);
            _ownedRoot.transform.SetParent(transform, false);

            Tugboat tugboat = _ownedRoot.AddComponent<Tugboat>();
            // Configure these serialized-only settings before NetworkManager.Awake initializes sockets.
            JsonUtility.FromJsonOverwrite("{\"_enableIpv6\":false,\"_stopSocketsOnThread\":false}", tugboat);
            tugboat.SetMaximumClients(MaximumPeers);
            tugboat.SetPort(port);
            tugboat.SetServerBindAddress(loopbackOnly ? "127.0.0.1" : "0.0.0.0", IPAddressType.IPv4);
            tugboat.SetClientAddress("127.0.0.1");

            TransportManager transportManager = _ownedRoot.AddComponent<TransportManager>();
            transportManager.Transport = tugboat;
            // FishNet defaults to 20 KiB. Allow 128 KiB of UTF-8 plus fragmentation/framing overhead.
            JsonUtility.FromJsonOverwrite("{\"_maximumClientPacketSize\":135168}", transportManager);
            _ownedRoot.AddComponent<ServerManager>().SetStartOnHeadless(false);

            _manager = _ownedRoot.AddComponent<NetworkManager>();
            // These are pinned FishNet 4.7.3 serialized fields. AllowMultiple never destroys another manager.
            JsonUtility.FromJsonOverwrite("{\"_dontDestroyOnLoad\":false,\"_persistence\":2,\"_runInBackground\":true}",
                _manager);
            _emptyPrefabs = ScriptableObject.CreateInstance<SinglePrefabObjects>();
            _emptyPrefabs.hideFlags = HideFlags.DontSave;
            _manager.SpawnablePrefabs = _emptyPrefabs;

            _ownedRoot.SetActive(true);
            if (_manager == null || !_manager.Initialized)
            {
                Stop();
                return false;
            }

            // FishNet reapplies its own rate on connection changes. Configure
            // both endpoints so host/client startup cannot remove our cap.
            _manager.ClientManager.SetFrameRate(TargetFrameRate);
            _manager.ServerManager.SetFrameRate(TargetFrameRate);

            _manager.ServerManager.RegisterBroadcast<CampEnvelope>(OnServerEnvelope, requireAuthentication: true);
            _manager.ClientManager.RegisterBroadcast<CampEnvelope>(OnClientEnvelope);
            _manager.ServerManager.RegisterBroadcast<CampVoiceEnvelope>(OnServerVoiceEnvelope, requireAuthentication: true);
            _manager.ClientManager.RegisterBroadcast<CampVoiceEnvelope>(OnClientVoiceEnvelope);
            _manager.ServerManager.OnAuthenticationResult += OnServerAuthentication;
            _manager.ServerManager.OnRemoteConnectionState += OnPeerConnectionState;
            _manager.ServerManager.OnServerConnectionState += OnServerConnectionState;
            _manager.ClientManager.OnAuthenticated += OnClientAuthenticated;
            _manager.ClientManager.OnClientConnectionState += OnClientConnectionState;
            _subscribed = true;
            return true;
        }

        private void Unsubscribe()
        {
            if (_subscribed && _manager != null && _manager.Initialized)
            {
                _manager.ServerManager.UnregisterBroadcast<CampEnvelope>(OnServerEnvelope);
                _manager.ClientManager.UnregisterBroadcast<CampEnvelope>(OnClientEnvelope);
                _manager.ServerManager.UnregisterBroadcast<CampVoiceEnvelope>(OnServerVoiceEnvelope);
                _manager.ClientManager.UnregisterBroadcast<CampVoiceEnvelope>(OnClientVoiceEnvelope);
                _manager.ServerManager.OnAuthenticationResult -= OnServerAuthentication;
                _manager.ServerManager.OnRemoteConnectionState -= OnPeerConnectionState;
                _manager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
                _manager.ClientManager.OnAuthenticated -= OnClientAuthenticated;
                _manager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
            }
            _subscribed = false;
        }

        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started && _startHostClient)
            {
                _startHostClient = false;
                if (!_manager.ClientManager.StartConnection("127.0.0.1", _port))
                    Stop();
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                _startHostClient = false;
                int[] disconnected = _peers.ToArray();
                _peers.Clear();
                foreach (int peer in disconnected)
                    PeerDisconnected?.Invoke(peer);
            }
        }

        private void OnServerAuthentication(NetworkConnection connection, bool authenticated)
        {
            if (!authenticated || !connection.IsActive || _peers.Contains(connection.ClientId))
                return;
            if (_peers.Count >= MaximumPeers)
            {
                connection.Disconnect(true);
                return;
            }
            _peers.Add(connection.ClientId);
        }

        private void OnPeerConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState == RemoteConnectionState.Stopped && _peers.Remove(args.ConnectionId))
                PeerDisconnected?.Invoke(args.ConnectionId);
        }

        private void OnClientAuthenticated()
        {
            if (_clientReady || _manager == null || !_manager.ClientManager.Connection.IsAuthenticated)
                return;
            _clientReady = true;
            ClientReady?.Invoke();
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Started)
                _clientReady = false;
            if (args.ConnectionState == LocalConnectionState.Stopped)
                ClientStopped?.Invoke();
        }

        private void OnServerEnvelope(NetworkConnection connection, CampEnvelope message, Channel channel)
        {
            if (channel == Channel.Reliable && connection.IsAuthenticated && connection.IsActive &&
                _peers.Contains(connection.ClientId) && IsValidPayload(message.Payload))
                ServerMessage?.Invoke(connection.ClientId, message.Payload);
        }

        private void OnClientEnvelope(CampEnvelope message, Channel channel)
        {
            if (channel == Channel.Reliable && IsClientConnected && IsValidPayload(message.Payload))
                ClientMessage?.Invoke(message.Payload);
        }

        private static bool IsValidPayload(string payload)
        {
            if (payload == null || payload.Length > MaximumPayloadBytes)
                return false;
            try
            {
                return PayloadEncoding.GetByteCount(payload) <= MaximumPayloadBytes;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        private void OnServerVoiceEnvelope(NetworkConnection connection, CampVoiceEnvelope message, Channel channel)
        {
            if (channel != Channel.Unreliable || !connection.IsAuthenticated || !connection.IsActive ||
                !_peers.Contains(connection.ClientId) || message.Data == null ||
                message.Data.Length == 0 || message.Data.Length > CampVoiceCodec.MaximumPacketBytes) return;
            ServerVoice?.Invoke(connection.ClientId, new CampVoiceFrame { Stream = message.Stream,
                Sequence = message.Sequence, Speaker = "", Data = message.Data });
        }
        private void OnClientVoiceEnvelope(CampVoiceEnvelope message, Channel channel)
        {
            if (channel != Channel.Unreliable || !IsClientConnected || message.Data == null ||
                message.Data.Length == 0 || message.Data.Length > CampVoiceCodec.MaximumPacketBytes ||
                message.Speaker == null || message.Speaker.Length != 32) return;
            ClientVoice?.Invoke(new CampVoiceFrame { Stream = message.Stream, Sequence = message.Sequence,
                Speaker = message.Speaker, Data = message.Data });
        }

        private void OnDisable() => Stop();
        private void OnDestroy() => Stop();
    }
}
