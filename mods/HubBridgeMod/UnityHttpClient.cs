using System;
using System.Reflection;
using System.Text;
using CesiumLoader.SDK.Runtime;

namespace HubBridgeMod
{
    internal sealed class HttpRequestHandle : IDisposable
    {
        private object _request;
        private object _asyncOp;
        private bool _disposed;

        private readonly PropertyInfo _reqIsDoneProp;
        private readonly PropertyInfo _opIsDoneProp;
        private readonly PropertyInfo _responseCodeProp;
        private readonly PropertyInfo _errorProp;
        private readonly PropertyInfo _resultProp;
        private readonly PropertyInfo _isNetworkErrorProp;
        private readonly PropertyInfo _downloadHandlerProp;
        private readonly MethodInfo _abortMethod;

        public long StatusCode { get; private set; }
        public string ResponseText { get; private set; }
        public string Error { get; private set; }
        public bool IsNetworkError { get; private set; }
        public bool IsCompleted { get; private set; }

        public HttpRequestHandle(
            object request,
            object asyncOp,
            PropertyInfo reqIsDoneProp,
            PropertyInfo opIsDoneProp,
            PropertyInfo responseCodeProp,
            PropertyInfo errorProp,
            PropertyInfo resultProp,
            PropertyInfo isNetworkErrorProp,
            PropertyInfo downloadHandlerProp,
            MethodInfo abortMethod)
        {
            _request = request;
            _asyncOp = asyncOp;
            _reqIsDoneProp = reqIsDoneProp;
            _opIsDoneProp = opIsDoneProp;
            _responseCodeProp = responseCodeProp;
            _errorProp = errorProp;
            _resultProp = resultProp;
            _isNetworkErrorProp = isNetworkErrorProp;
            _downloadHandlerProp = downloadHandlerProp;
            _abortMethod = abortMethod;
            ResponseText = "";
            Error = "";
        }

        public bool Poll()
        {
            if (_disposed)
            {
                IsCompleted = true;
                return true;
            }
            if (IsCompleted) return true;
            if (_request == null)
            {
                IsCompleted = true;
                IsNetworkError = true;
                Error = "request_null";
                return true;
            }

            bool done = false;
            try
            {
                if (_reqIsDoneProp != null)
                {
                    object val = _reqIsDoneProp.GetValue(_request, null);
                    if (val is bool) done = (bool)val;
                }
                else if (_asyncOp != null && _opIsDoneProp != null)
                {
                    object val = _opIsDoneProp.GetValue(_asyncOp, null);
                    if (val is bool) done = (bool)val;
                }
                else
                {
                    done = true;
                }
            }
            catch (Exception ex)
            {
                IsCompleted = true;
                IsNetworkError = true;
                Error = ex.Message ?? "poll_failed";
                return true;
            }

            if (!done) return false;

            ReadResult();
            IsCompleted = true;
            return true;
        }

        private void ReadResult()
        {
            try
            {
                if (_responseCodeProp != null)
                {
                    object codeObj = _responseCodeProp.GetValue(_request, null);
                    if (codeObj != null) StatusCode = Convert.ToInt64(codeObj);
                }
            }
            catch { StatusCode = 0; }

            try
            {
                if (_errorProp != null)
                {
                    string err = _errorProp.GetValue(_request, null) as string;
                    Error = err ?? "";
                }
            }
            catch { Error = ""; }

            try
            {
                if (_downloadHandlerProp != null)
                {
                    object dh = _downloadHandlerProp.GetValue(_request, null);
                    if (dh != null)
                    {
                        PropertyInfo textProp = dh.GetType().GetProperty("text");
                        if (textProp != null)
                        {
                            string text = textProp.GetValue(dh, null) as string;
                            ResponseText = text ?? "";
                        }
                    }
                }
            }
            catch { ResponseText = ""; }

            bool netErr = false;
            try
            {
                if (_resultProp != null)
                {
                    object resObj = _resultProp.GetValue(_request, null);
                    if (resObj != null)
                    {
                        string resName = resObj.ToString();
                        if (string.Equals(resName, "ConnectionError", StringComparison.Ordinal)
                            || string.Equals(resName, "DataProcessingError", StringComparison.Ordinal))
                        {
                            netErr = true;
                        }
                    }
                }
                else if (_isNetworkErrorProp != null)
                {
                    object netObj = _isNetworkErrorProp.GetValue(_request, null);
                    if (netObj is bool) netErr = (bool)netObj;
                }
            }
            catch { }

            if (!netErr && StatusCode <= 0 && !string.IsNullOrEmpty(Error))
            {
                netErr = true;
            }
            IsNetworkError = netErr;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!IsCompleted && _request != null && _abortMethod != null)
                {
                    _abortMethod.Invoke(_request, null);
                }
            }
            catch { }

            try
            {
                IDisposable disp = _request as IDisposable;
                if (disp != null) disp.Dispose();
            }
            catch { }

            _request = null;
            _asyncOp = null;
        }
    }

    internal static class UnityHttpClient
    {
        private static bool _resolved;
        private static Type _uwrType;
        private static ConstructorInfo _uwrCtor;
        private static ConstructorInfo _uploadCtor;
        private static ConstructorInfo _downloadCtor;
        private static PropertyInfo _uploadHandlerProp;
        private static PropertyInfo _downloadHandlerProp;
        private static PropertyInfo _uploadContentTypeProp;
        private static PropertyInfo _timeoutProp;
        private static MethodInfo _setHeaderMethod;
        private static MethodInfo _sendMethod;
        private static MethodInfo _abortMethod;
        private static PropertyInfo _reqIsDoneProp;
        private static PropertyInfo _responseCodeProp;
        private static PropertyInfo _errorProp;
        private static PropertyInfo _resultProp;
        private static PropertyInfo _isNetworkErrorProp;
        private static PropertyInfo _opIsDoneProp;

        public static bool EnsureResolved(out string error)
        {
            if (!_resolved)
            {
                _resolved = true;
                try
                {
                    _uwrType = RuntimeAssemblyService.FindTypeBySimpleName("UnityWebRequest");
                    Type uploadType = RuntimeAssemblyService.FindTypeBySimpleName("UploadHandlerRaw");
                    Type downloadType = RuntimeAssemblyService.FindTypeBySimpleName("DownloadHandlerBuffer");

                    if (_uwrType != null)
                    {
                        _uwrCtor = _uwrType.GetConstructor(new Type[] { typeof(string), typeof(string) });
                        _uploadHandlerProp = _uwrType.GetProperty("uploadHandler");
                        _downloadHandlerProp = _uwrType.GetProperty("downloadHandler");
                        _timeoutProp = _uwrType.GetProperty("timeout");
                        _setHeaderMethod = _uwrType.GetMethod("SetRequestHeader", new Type[] { typeof(string), typeof(string) });
                        _sendMethod = _uwrType.GetMethod("SendWebRequest", Type.EmptyTypes);
                        _abortMethod = _uwrType.GetMethod("Abort", Type.EmptyTypes);
                        _reqIsDoneProp = _uwrType.GetProperty("isDone");
                        _responseCodeProp = _uwrType.GetProperty("responseCode");
                        _errorProp = _uwrType.GetProperty("error");
                        _resultProp = _uwrType.GetProperty("result");
                        _isNetworkErrorProp = _uwrType.GetProperty("isNetworkError");
                    }

                    if (uploadType != null)
                    {
                        _uploadCtor = uploadType.GetConstructor(new Type[] { typeof(byte[]) });
                        _uploadContentTypeProp = uploadType.GetProperty("contentType");
                    }

                    if (downloadType != null)
                    {
                        _downloadCtor = downloadType.GetConstructor(Type.EmptyTypes);
                    }
                }
                catch { }
            }

            if (_uwrType == null || _uwrCtor == null || _sendMethod == null)
            {
                error = "UnityWebRequest 不可用";
                return false;
            }
            if (_uploadCtor == null || _downloadCtor == null || _uploadHandlerProp == null || _downloadHandlerProp == null)
            {
                error = "UnityWebRequest UploadHandlerRaw/DownloadHandlerBuffer 不可用";
                return false;
            }
            error = null;
            return true;
        }

        public static HttpRequestHandle StartPostJson(
            string url,
            string jsonBody,
            string bridgeToken,
            int timeoutSeconds,
            out string startError)
        {
            startError = null;
            if (!EnsureResolved(out startError)) return null;

            object req = null;
            try
            {
                req = _uwrCtor.Invoke(new object[] { url, "POST" });
                byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody ?? "{}");
                object uploadHandler = _uploadCtor.Invoke(new object[] { bodyBytes });
                if (_uploadContentTypeProp != null)
                {
                    try { _uploadContentTypeProp.SetValue(uploadHandler, "application/json; charset=utf-8", null); }
                    catch { }
                }
                object downloadHandler = _downloadCtor.Invoke(null);

                _uploadHandlerProp.SetValue(req, uploadHandler, null);
                _downloadHandlerProp.SetValue(req, downloadHandler, null);

                if (_timeoutProp != null && timeoutSeconds > 0)
                {
                    try { _timeoutProp.SetValue(req, timeoutSeconds, null); }
                    catch { }
                }

                if (_setHeaderMethod != null)
                {
                    _setHeaderMethod.Invoke(req, new object[] { "Content-Type", "application/json; charset=utf-8" });
                    if (!string.IsNullOrEmpty(bridgeToken))
                    {
                        _setHeaderMethod.Invoke(req, new object[] { "X-Bridge-Token", bridgeToken });
                    }
                }

                object asyncOp = _sendMethod.Invoke(req, null);
                if (asyncOp != null && _opIsDoneProp == null)
                {
                    _opIsDoneProp = asyncOp.GetType().GetProperty("isDone");
                }

                return new HttpRequestHandle(
                    req,
                    asyncOp,
                    _reqIsDoneProp,
                    _opIsDoneProp,
                    _responseCodeProp,
                    _errorProp,
                    _resultProp,
                    _isNetworkErrorProp,
                    _downloadHandlerProp,
                    _abortMethod);
            }
            catch (Exception ex)
            {
                try
                {
                    IDisposable disp = req as IDisposable;
                    if (disp != null) disp.Dispose();
                }
                catch { }
                startError = ex.Message ?? "send_failed";
                return null;
            }
        }
    }
}
