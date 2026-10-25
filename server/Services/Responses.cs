using System.Text.Json;

namespace MapBuilder.Server.Services
{
    /// <summary>Builds the JSON reply strings every handler returns.</summary>
    public static class Responses
    {
        public static string Success(string message)
            => JsonSerializer.Serialize(new CommandResponse { Success = true, Message = message });

        public static string Error(string error)
            => JsonSerializer.Serialize(new CommandResponse { Success = false, Error = error });

        /// <summary>Serialize an arbitrary <see cref="CommandResponse"/>.</summary>
        public static string Json(CommandResponse response)
            => JsonSerializer.Serialize(response);
    }
}
