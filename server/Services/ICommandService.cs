namespace MapBuilder.Server.Services
{
    /// <summary>A group of commands that registers its handlers with the <see cref="CommandRegistry"/> at startup.</summary>
    internal interface ICommandService
    {
        void Register(CommandRegistry registry);
    }
}
