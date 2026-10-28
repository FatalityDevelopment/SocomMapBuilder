using System.Text.Json;

namespace MapBuilder.Server.Services
{
    /// <summary>Builds the JSON reply strings every handler returns.</summary>
    public static class Responses
    {
        /// <summary>The camelCase options every command's <c>Data</c> is written with.</summary>
        public static readonly JsonSerializerOptions DataOptions = new(JsonSerializerDefaults.Web);

        /// <summary>A success reply with <paramref name="data"/> serialized into <c>Data</c>.</summary>
        public static string Ok(string command, object data)
            => Json(new CommandResponse { Success = true, Command = command, Data = JsonSerializer.Serialize(data, DataOptions) });

        /// <summary>Runs <paramref name="body"/>; its result is the reply's data, an exception its error.</summary>
        public static string Run(string command, Func<object> body)
        {
            try { return Ok(command, body()); }
            catch (Exception ex) { return Error($"{command}: {ex.Message}"); }
        }

        public static string Success(string message)
            => JsonSerializer.Serialize(new CommandResponse { Success = true, Message = message });

        public static string Error(string error)
            => JsonSerializer.Serialize(new CommandResponse { Success = false, Error = error });

        /// <summary>Serialize an arbitrary <see cref="CommandResponse"/>.</summary>
        public static string Json(CommandResponse response)
            => JsonSerializer.Serialize(response);
    }
}
