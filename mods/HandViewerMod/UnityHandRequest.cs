using System;
using System.Reflection;
using CesiumLoader.SDK.Runtime;

namespace HandViewerMod
{
    internal sealed class UnityHandRequest : IHandRequest
    {
        readonly object request;
        readonly long deadline;
        bool disposed;
        public UnityHandRequest(string endpoint, string code, int timeout)
        {
            string url = ServiceAddress.QueryUrl(endpoint, code);
            var type = RuntimeAssemblyService.FindTypeBySimpleName("UnityWebRequest");
            if (type == null) throw new NotSupportedException("UnityWebRequest 不可用");
            var get = type.GetMethod("Get", new[] { typeof(string) });
            request = get.Invoke(null, new object[] { url });
            deadline = DateTime.UtcNow.Ticks / 10000 + timeout * 1000L;
            try
            {
                Set("timeout", timeout);
                type.GetMethod("SetRequestHeader", new[] { typeof(string), typeof(string) })
                    .Invoke(request, new object[] { "User-Agent", ModEntry.UserAgent });
                Call("SendWebRequest");
            }
            catch { Dispose(); throw; }
        }
        object Get(string name) => request.GetType().GetProperty(name).GetValue(request, null);
        void Set(string name, object value) => request.GetType().GetProperty(name).SetValue(request, value, null);
        object Call(string name) => request.GetType().GetMethod(name, Type.EmptyTypes).Invoke(request, null);
        public bool Done
        {
            get
            {
                if (disposed) return true;
                if (Convert.ToUInt64(Get("downloadedBytes")) > 1024 * 1024) throw new Exception("响应过大");
                return (bool)Get("isDone") || DateTime.UtcNow.Ticks / 10000 >= deadline;
            }
        }
        public long Code => (bool)Get("isDone") ? Convert.ToInt64(Get("responseCode")) : 0;
        public string Text
        {
            get
            {
                var bytes = Convert.ToUInt64(Get("downloadedBytes"));
                if (bytes > 1024 * 1024) throw new Exception("响应过大");
                var handler = Get("downloadHandler");
                return (string)handler.GetType().GetProperty("text").GetValue(handler, null);
            }
        }
        public string RetryAfter => (string)request.GetType().GetMethod("GetResponseHeader", new[] { typeof(string) }).Invoke(request, new object[] { "Retry-After" });
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { Call("Abort"); } finally { Call("Dispose"); }
        }
    }
}
