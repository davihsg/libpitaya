using System;
using System.Collections.Generic;
using System.IO;
using Google.Protobuf;
using UnityEngine;

namespace Pitaya
{
    public class PitayaClient : IDisposable, IPitayaListener
    {
        public event Action<PitayaNetWorkState, NetworkError> NetWorkStateChangedEvent;

        private const int DefaultConnectionTimeout = 30;

        private IntPtr _client = IntPtr.Zero;
        private PitayaMetrics _metricsAggr;
        private EventManager _eventManager;
        private bool _disposed;
        private uint _reqUid;
        private Dictionary<uint, Action<string, string>> _requestHandlers;
        private TypeSubscriber<uint> _typeRequestSubscriber;
        private TypeSubscriber<string> _typePushSubscriber;

        public PitayaClient()
        {
            Init(null, false, false, false, DefaultConnectionTimeout, null);
        }

        public PitayaClient(int connectionTimeout)
        {
            Init(null, false, false, false, connectionTimeout, null);
        }

        public PitayaClient(string certificateName = null)
        {
            Init(certificateName, certificateName != null, false, false, DefaultConnectionTimeout, null);
        }

        public PitayaClient(PitayaMetrics.Config config = null)
        {
            Init(null, false, false, false, DefaultConnectionTimeout, config);
        }

        public PitayaClient(bool enableReconnect = false, string certificateName = null, int connectionTimeout = DefaultConnectionTimeout, PitayaMetrics.Config config = null)
        {
            Init(certificateName, certificateName != null, false, enableReconnect, DefaultConnectionTimeout, config);
        }

        ~PitayaClient()
        {
            Dispose();
        }

        private void Init(
            string certificateName,
            bool enableTlS,
            bool enablePolling,
            bool enableReconnect,
            int connTimeout,
            PitayaMetrics.Config config)
        {
            _eventManager = new EventManager();
            _typeRequestSubscriber = new TypeSubscriber<uint>();
            _typePushSubscriber = new TypeSubscriber<string>();
            _client = PitayaBinding.CreateClient(enableTlS, enablePolling, enableReconnect, connTimeout, this);

            if (config != null)
            {
                _metricsAggr = new PitayaMetrics(config);
            }

            if (certificateName != null)
            {
#if UNITY_EDITOR
                if (File.Exists(certificateName))
                    PitayaBinding.SetCertificatePath(certificateName);
                else
                    PitayaBinding.SetCertificateName(certificateName);
#else
                PitayaBinding.SetCertificateName(certificateName);
#endif
            }
        }

        public static void SetLogLevel(PitayaLogLevel level)
        {
            PitayaBinding.SetLogLevel(level);
        }

        public int Quality
        {
            get { return PitayaBinding.Quality(_client); }
        }


        public PitayaClientState State
        {
            get { return PitayaBinding.State(_client); }
        }

        public void Connect(string host, int port, string handshakeOpts = null)
        {
            if (_metricsAggr != null) _metricsAggr.Start();
            PitayaBinding.Connect(_client, host, port, handshakeOpts);
        }

        public void Connect(string host, int port, Dictionary<string, string> handshakeOpts)
        {
            if (_metricsAggr != null) _metricsAggr.Start();
            var opts = Pitaya.SimpleJson.SimpleJson.SerializeObject(handshakeOpts);
            PitayaBinding.Connect(_client, host, port, opts);
        }

        public void Request(string route, Action<string> action, Action<PitayaError> errorAction)
        {
            Request(route, (string)null, action, errorAction);
        }

        public void Request<T>(string route, Action<T> action, Action<PitayaError> errorAction)
        {
            Request(route, null, action, errorAction);
        }

        public void Request(string route, string msg, Action<string> action, Action<PitayaError> errorAction)
        {
            Request(route, msg, -1, action, errorAction);
        }

        public void Request<T>(string route, IMessage msg, Action<T> action, Action<PitayaError> errorAction)
        {
            Request(route, msg, -1, action, errorAction);
        }

        public void Request<T>(string route, IMessage msg, int timeout, Action<T> action, Action<PitayaError> errorAction)
        {
            if (_metricsAggr != null) _metricsAggr.StartRecordingRequest(route);

            _reqUid++;
            _typeRequestSubscriber.Subscribe(_reqUid, typeof(T));

            void ResponseAction(object res)
            {
                if (_metricsAggr != null) _metricsAggr.StopRecordingRequest(route);
                action((T) res);
            }

            void ErrorAction(PitayaError err)
            {
                if (_metricsAggr != null) _metricsAggr.StopRecordingRequest(route, err);
                errorAction(err);
            }

            _eventManager.AddCallBack(_reqUid, ResponseAction, ErrorAction);

            var serializer = PitayaBinding.ClientSerializer(_client);

            PitayaBinding.Request(_client, route, ProtobufSerializer.Encode(msg, serializer), _reqUid, timeout);
        }

        public void Request(string route, string msg, int timeout, Action<string> action, Action<PitayaError> errorAction)
        {
            if (_metricsAggr != null) _metricsAggr.StartRecordingRequest(route);

            _reqUid++;

            void ResponseAction(object res)
            {
                _metricsAggr.StopRecordingRequest(route);
                action((string) res);
            }

            void ErrorAction(PitayaError err)
            {
                _metricsAggr.StopRecordingRequest(route, err);
                errorAction(err);
            }

            _eventManager.AddCallBack(_reqUid, ResponseAction, ErrorAction);

            PitayaBinding.Request(_client, route,JsonSerializer.Encode(msg), _reqUid, timeout);
        }

        public void Notify(string route, IMessage msg)
        {
            Notify(route, -1, msg);
        }

        public void Notify(string route, int timeout, IMessage msg)
        {
            var serializer = PitayaBinding.ClientSerializer(_client);
            PitayaBinding.Notify(_client, route, ProtobufSerializer.Encode(msg,serializer), timeout);
        }

        public void Notify(string route, string msg)
        {
            Notify(route, -1, msg);
        }

        public void Notify(string route, int timeout, string msg)
        {
            PitayaBinding.Notify(_client, route, JsonSerializer.Encode(msg), timeout);
        }

        public void OnRoute(string route, Action<string> action)
        {
            void ResponseAction(object res)
            {
                action((string) res);
            }

            _eventManager.AddOnRouteEvent(route, ResponseAction);
        }

        // start listening to a route
        public void OnRoute<T>(string route, Action<T> action)
        {
            _typePushSubscriber.Subscribe(route, typeof(T));

            void ResponseAction(object res)
            {
                action((T) res);
            }

            _eventManager.AddOnRouteEvent(route, ResponseAction);
        }

        public void OffRoute(string route)
        {
            _eventManager.RemoveOnRouteEvent(route);
        }

        public void Disconnect()
        {
            PitayaBinding.Disconnect(_client);
        }

        //---------------Pitaya Listener------------------------//

        public void OnRequestResponse(uint rid, byte[] data)
        {
            object decoded;
            if (_typeRequestSubscriber.HasType(rid))
            {
                var type = _typeRequestSubscriber.GetType(rid);
                decoded = ProtobufSerializer.Decode(data, type, PitayaBinding.ClientSerializer(_client));
            }
            else
            {
                decoded = JsonSerializer.Decode(data);
            }

            _eventManager.InvokeCallBack(rid, decoded);
        }

        public void OnRequestError(uint rid, PitayaError error)
        {
            _eventManager.InvokeErrorCallBack(rid, error);
        }

        public void OnNetworkEvent(PitayaNetWorkState state, NetworkError error)
        {
            if (_metricsAggr != null) _metricsAggr.Update(state, error);
            if(NetWorkStateChangedEvent != null ) NetWorkStateChangedEvent.Invoke(state, error);
        }

        public void OnUserDefinedPush(string route, byte[] serializedBody)
        {
            object decoded;
            if (_typePushSubscriber.HasType(route))
            {
                var type = _typePushSubscriber.GetType(route);
                decoded = ProtobufSerializer.Decode(serializedBody, type, PitayaBinding.ClientSerializer(_client));
            }
            else
            {
                decoded = JsonSerializer.Decode(serializedBody);
            }

            _eventManager.InvokeOnEvent(route, decoded);
        }

        public void Dispose()
        {
            Debug.Log(string.Format("PitayaClient Disposed {0}", _client));
            if (_disposed)
                return;

            if(_eventManager != null ) _eventManager.Dispose();

            _reqUid = 0;
            PitayaBinding.Disconnect(_client);
            if (_metricsAggr != null) _metricsAggr.ForceStop();
            PitayaBinding.Dispose(_client);

            _client = IntPtr.Zero;
            _disposed = true;
        }
    }
}
