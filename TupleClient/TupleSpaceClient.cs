using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TupleClient;

public class TupleSpaceClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _spaceName;
    private readonly string? _keyId;
    private readonly string? _privateKeyPem;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Lock _scopeLock = new();
    private BulkOutScope? _currentBulkScope;

    public TupleSpaceClient(string host, int port, string spaceName, string? keyId = null, string? privateKeyPem = null)
    {
        _host = host;
        _port = port;
        _spaceName = spaceName;
        _keyId = keyId;
        _privateKeyPem = privateKeyPem;
    }

    internal void EnterBulkOutScope(BulkOutScope scope)
    {
        lock (_scopeLock)
        {
            if (_currentBulkScope != null)
                throw new InvalidOperationException("Already in a bulk out scope");
            _currentBulkScope = scope;
        }
    }

    internal void ExitBulkOutScope(BulkOutScope scope)
    {
        lock (_scopeLock)
        {
            if (_currentBulkScope == scope)
                _currentBulkScope = null;
        }
    }

    internal async Task SendBulkOutAsync(List<string[]> tuples)
    {
        await _semaphore.WaitAsync();
        try
        {
            await EnsureConnectedAsync();
            var command = new { Type = "OUTBULK", SpaceName = _spaceName, Tuples = tuples };
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(command));
            var line = await _reader!.ReadLineAsync();
            if (line == null) throw new Exception("Disconnected from server");
            var response = JsonSerializer.Deserialize<Response>(line) ?? throw new Exception("Invalid response");
            if (response.Status != "OK") throw new Exception(response.Message);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (_client == null || !_client.Connected)
        {
            _client = new TcpClient();
            await _client.ConnectAsync(_host, _port, cancellationToken);
            _stream = _client.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
            _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };

            if (_keyId != null && _privateKeyPem != null)
            {
                // 1. Send AUTH
                var authCommand = new { Type = "AUTH", KeyId = _keyId };
                await _writer.WriteLineAsync(JsonSerializer.Serialize(authCommand));

                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line == null) throw new Exception("Disconnected during authentication");
                var response = JsonSerializer.Deserialize<Response>(line) ?? throw new Exception("Invalid auth response");

                if (response.Status == "CHALLENGE")
                {
                    // 2. Sign Challenge
                    var challenge = Convert.FromBase64String(response.Challenge ?? throw new Exception("Challenge missing"));
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(_privateKeyPem);
                    var signature = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var sigCommand = new { Type = "RESPONSE", Signature = Convert.ToBase64String(signature) };
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(sigCommand));

                    line = await _reader.ReadLineAsync(cancellationToken);
                    if (line == null) throw new Exception("Disconnected during challenge response");
                    response = JsonSerializer.Deserialize<Response>(line) ?? throw new Exception("Invalid response to challenge");
                }

                if (response.Status != "OK")
                {
                    throw new Exception($"Authentication failed: {response.Message}");
                }
            }

            // Create/Ensure space exists
            var createCommand = new { Type = "CREATE", SpaceName = _spaceName, Tuple = (string[]?)null };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(createCommand));
            var createLine = await _reader.ReadLineAsync(cancellationToken);
            if (createLine == null) throw new Exception("Disconnected from server during CREATE");
        }
    }

    private async Task<Response> SendCommandAsync(string type, string[]? tuple, CancellationToken cancellationToken = default)
    {
        if (_currentBulkScope != null) throw new InvalidOperationException($"Cannot perform {type} operation while in a bulk out scope");
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            var command = new { Type = type, SpaceName = _spaceName, Tuple = tuple };
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(command));
            
            var line = await _reader!.ReadLineAsync(cancellationToken);
            if (line == null) throw new Exception("Disconnected from server");
            return JsonSerializer.Deserialize<Response>(line) ?? throw new Exception("Invalid response");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<string[]> OutAsync(params string[] tuple)
    {
        if (_currentBulkScope != null)
        {
            _currentBulkScope.AddTuple(tuple);
            return tuple;
        }
        var response = await SendCommandAsync("OUT", tuple);
        if (response.Status != "OK") throw new Exception(response.Message);
        return tuple;
    }

    public async Task<string[]> OutAsync<T>(T tuple) where T : struct
    {
        var properties = typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var values = new string[properties.Length + 1];
        values[0] = typeof(T).Name;
        for (int i = 0; i < properties.Length; i++)
        {
            values[i + 1] = properties[i].GetValue(tuple)?.ToString() ?? "";
        }
        return await OutAsync(values);
    }

    public async Task EvalAsync<T>(T liveTuple) where T : struct
    {
        var properties = typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var evaluationTasks = new List<(int Index, Task<object?> Task)>();
        var values = new string[properties.Length + 1];
        values[0] = typeof(T).Name;

        for (var i = 0; i < properties.Length; i++)
        {
            var value = properties[i].GetValue(liveTuple);
            if (value is Delegate del)
            {
                var task = Task.Run(async () =>
                {
                    try
                    {
                        var result = del.DynamicInvoke();
                        if (result is Task t)
                        {
                            await t.ConfigureAwait(false);
                            var resultProperty = t.GetType().GetProperty("Result");
                            return resultProperty?.GetValue(t);
                        }
                        return result;
                    }
                    catch (Exception ex)
                    {
                        return ex;
                    }
                });
                evaluationTasks.Add((i + 1, task));
            }
            else
            {
                values[i + 1] = value?.ToString() ?? "";
            }
        }

        if (evaluationTasks.Count == 0)
        {
            await OutAsync(values).ConfigureAwait(false);
            return;
        }

        var hostToUse = _host;
        var portToUse = _port;
        var spaceNameToUse = _spaceName;
        var keyIdToUse = _keyId;
        var privateKeyPemToUse = _privateKeyPem;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(evaluationTasks.Select(t => t.Task)).ConfigureAwait(false);
                foreach (var (index, task) in evaluationTasks)
                {
                    var result = await task.ConfigureAwait(false);
                    if (result is Exception)
                    {
                         values[index] = $"Error: {((Exception)result).Message}";
                    }
                    else
                    {
                        values[index] = result?.ToString() ?? "";
                    }
                }

                using var client = new TupleSpaceClient(hostToUse, portToUse, spaceNameToUse, keyIdToUse, privateKeyPemToUse);
                await client.OutAsync(values).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // In a production system, we'd use a real logger
            }
        });
    }

    public async Task<string[]> InAsync(params string[] pattern)
    {
        return await InAsync(Timeout.InfiniteTimeSpan, pattern);
    }

    public async Task<T> InAsync<T>(params object[] pattern) where T : struct
    {
        var stringPattern = ConvertPatternToStrings(pattern);
        var fullPattern = new string[stringPattern.Length + 1];
        fullPattern[0] = typeof(T).Name;
        for (var i = 0; i < stringPattern.Length; i++)
            fullPattern[i + 1] = stringPattern[i];
        var result = await InAsync(fullPattern);
        return MapToStruct<T>(result);
    }

    private static string[] ConvertPatternToStrings(object[] pattern)
    {
        var result = new string[pattern.Length];
        for (var i = 0; i < pattern.Length; i++)
        {
            result[i] = pattern[i].ToString() ?? "";
        }
        return result;
    }

    public async Task<string[]> InAsync(TimeSpan timeout, params string[] pattern)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var response = await SendCommandAsync("IN", pattern, cts.Token);
            if (response.Status != "OK") throw new Exception(response.Message);
            return response.Tuple ?? throw new Exception("No tuple returned");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"InAsync timed out after {timeout}");
        }
    }

    public async Task<T> InAsync<T>(TimeSpan timeout, params object[] pattern) where T : struct
    {
        var stringPattern = ConvertPatternToStrings(pattern);
        var fullPattern = new string[stringPattern.Length + 1];
        fullPattern[0] = typeof(T).Name;
        for (int i = 0; i < stringPattern.Length; i++)
            fullPattern[i + 1] = stringPattern[i];
        var result = await InAsync(timeout, fullPattern);
        return MapToStruct<T>(result);
    }

    public async Task<string[]> RdAsync(params string[] pattern)
    {
        return await RdAsync(Timeout.InfiniteTimeSpan, pattern);
    }

    public async Task<T> RdAsync<T>(params object[] pattern) where T : struct
    {
        var stringPattern = ConvertPatternToStrings(pattern);
        var fullPattern = new string[stringPattern.Length + 1];
        fullPattern[0] = typeof(T).Name;
        for (int i = 0; i < stringPattern.Length; i++)
            fullPattern[i + 1] = stringPattern[i];
        var result = await RdAsync(fullPattern);
        return MapToStruct<T>(result);
    }

    public async Task<string[]> RdAsync(TimeSpan timeout, params string[] pattern)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var response = await SendCommandAsync("RD", pattern, cts.Token);
            if (response.Status != "OK") throw new Exception(response.Message);
            return response.Tuple ?? throw new Exception("No tuple returned");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"RdAsync timed out after {timeout}");
        }
    }

    public async Task<T> RdAsync<T>(TimeSpan timeout, params object[] pattern) where T : struct
    {
        var stringPattern = ConvertPatternToStrings(pattern);
        var fullPattern = new string[stringPattern.Length + 1];
        fullPattern[0] = typeof(T).Name;
        for (int i = 0; i < stringPattern.Length; i++)
            fullPattern[i + 1] = stringPattern[i];
        var result = await RdAsync(timeout, fullPattern);
        return MapToStruct<T>(result);
    }

    public async Task<string[]?> InpAsync(params string[] pattern)
    {
        var response = await SendCommandAsync("INP", pattern);
        if (response.Status == "OK") return response.Tuple;
        return null;
    }

    public async Task<T?> InpAsync<T>(params object[] pattern) where T : struct
    {
        var stringPattern = ConvertPatternToStrings(pattern);
        var fullPattern = new string[stringPattern.Length + 1];
        fullPattern[0] = typeof(T).Name;
        for (int i = 0; i < stringPattern.Length; i++)
            fullPattern[i + 1] = stringPattern[i];
        var result = await InpAsync(fullPattern);
        return result == null ? null : MapToStruct<T>(result);
    }

    public async Task<string[]?> RdpAsync(params string[] pattern)
    {
        var response = await SendCommandAsync("RDP", pattern);
        if (response.Status == "OK") return response.Tuple;
        return null;
    }

    public async Task<T?> RdpAsync<T>(params object[] pattern) where T : struct
    {
        var stringPattern = ConvertPatternToStrings(pattern);
        var fullPattern = new string[stringPattern.Length + 1];
        fullPattern[0] = typeof(T).Name;
        for (int i = 0; i < stringPattern.Length; i++)
            fullPattern[i + 1] = stringPattern[i];
        var result = await RdpAsync(fullPattern);
        return result == null ? null : MapToStruct<T>(result);
    }

    private T MapToStruct<T>(string[] values) where T : struct
    {
        var properties = typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (values.Length != properties.Length + 1)
            throw new Exception($"Tuple length {values.Length} does not match struct {typeof(T).Name} property count {properties.Length} (+1 for type name)");

        if (values[0] != typeof(T).Name)
            throw new Exception($"Tuple type name {values[0]} does not match expected {typeof(T).Name}");

        object obj = default(T);
        for (int i = 0; i < properties.Length; i++)
        {
            var propertyType = properties[i].PropertyType;
            var val = Convert.ChangeType(values[i + 1], propertyType);
            properties[i].SetValue(obj, val);
        }
        return (T)obj;
    }
  
    public async Task RunRemotelyAsync(string code, string tupleDefinitions, int workerId)
    {
        using var expressionClient = new TupleSpaceClient(_host, _port, "expressions", _keyId, _privateKeyPem);
        await expressionClient.OutAsync("expression", _spaceName, code, tupleDefinitions, workerId.ToString());
    }

    public async Task UploadPublicKeyAsync(string keyId, string publicKeyPem)
    {
        await _semaphore.WaitAsync();
        try
        {
            await EnsureConnectedAsync();
            var command = new { Type = "UPLOAD_KEY", KeyId = keyId, PublicKey = publicKeyPem };
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(command));
            var line = await _reader!.ReadLineAsync();
            if (line == null) throw new Exception("Disconnected from server");
            var response = JsonSerializer.Deserialize<Response>(line) ?? throw new Exception("Invalid response");
            if (response.Status != "OK") throw new Exception(response.Message);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task RevokePublicKeyAsync(string keyId)
    {
        await _semaphore.WaitAsync();
        try
        {
            await EnsureConnectedAsync();
            var command = new { Type = "REVOKE_KEY", KeyId = keyId };
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(command));
            var line = await _reader!.ReadLineAsync();
            if (line == null) throw new Exception("Disconnected from server");
            var response = JsonSerializer.Deserialize<Response>(line) ?? throw new Exception("Invalid response");
            if (response.Status != "OK") throw new Exception(response.Message);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
        _semaphore.Dispose();
    }

    private class Response
    {
        public string Status { get; set; } = "";
        public string? Message { get; set; }
        public string[]? Tuple { get; set; }
        public string? Challenge { get; set; }
    }
}
