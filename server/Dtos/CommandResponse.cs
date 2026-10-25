namespace MapBuilder.Server
{
    /// <summary>Reply to one command. <see cref="Data"/> is the command's result as a JSON string.</summary>
    public class CommandResponse
    {
        public bool Success { get; set; }
        public string? Command { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
        public string? Data { get; set; }
    }
}
