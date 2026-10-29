using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Clircs.Identity;
using Clircs.Sessions;

namespace Clircs.Commands;

public sealed record CommandContext(NetworkSessionId? NetworkSessionId, BufferId? BufferId);

public sealed record CommandResult(bool Succeeded, string? Message = null, PresentationBlock? Presentation = null)
{
    public static CommandResult Success(string? message = null) => new(true, message);

    public static CommandResult Success(PresentationBlock presentation) => new(true, Presentation: presentation);

    public static CommandResult Failure(string message) => new(false, message);
}

public delegate ValueTask<CommandResult> CommandHandler(CommandContext context, CommandInput input, CancellationToken cancellationToken);

public enum CommandArgumentMode
{
    Tokenized,
    Text
}

public sealed class CommandDefinition
{
    public CommandDefinition(
        string name,
        IEnumerable<string> aliases,
        string usage,
        string description,
        CommandHandler handler,
        bool visibleInHelp = true,
        IEnumerable<PresentationField>? extendedHelp = null,
        Func<string, PresentationBlock?>? topicHelp = null,
        CommandArgumentMode argumentMode = CommandArgumentMode.Tokenized)
    {
        if (!Enum.IsDefined(argumentMode))
        {
            throw new ArgumentOutOfRangeException(nameof(argumentMode));
        }

        Name = CommandLineParser.NormalizeName(name);
        ArgumentNullException.ThrowIfNull(aliases);
        Aliases = new ReadOnlyCollection<string>(aliases.Select(CommandLineParser.NormalizeName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Usage = usage;
        Description = description;
        VisibleInHelp = visibleInHelp;
        ExtendedHelp = new ReadOnlyCollection<PresentationField>((extendedHelp ?? []).ToArray());
        TopicHelp = topicHelp;
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        ArgumentMode = argumentMode;
    }

    public string Name { get; }

    public IReadOnlyList<string> Aliases { get; }

    public string Usage { get; }

    public string Description { get; }

    public bool VisibleInHelp { get; }

    public IReadOnlyList<PresentationField> ExtendedHelp { get; }

    public Func<string, PresentationBlock?>? TopicHelp { get; }

    public CommandHandler Handler { get; }

    public CommandArgumentMode ArgumentMode { get; }
}

public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CommandDefinition> _definitions = [];
    private readonly object _gate = new();

    public IReadOnlyList<CommandDefinition> Definitions
    {
        get
        {
            lock (_gate)
            {
                return _definitions.ToArray();
            }
        }
    }

    public ParsedInput Parse(string input) =>
        CommandLineParser.Parse(
            input,
            name => TryResolve(name, out var definition)
                ? definition.ArgumentMode
                : CommandArgumentMode.Tokenized);

    public void Register(CommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            var names = new[] { definition.Name }.Concat(definition.Aliases).ToArray();
            var collision = names.FirstOrDefault(_registrations.ContainsKey);
            if (collision is not null)
            {
                throw new InvalidOperationException($"Command name or alias '/{collision}' is already registered.");
            }

            foreach (var name in names)
            {
                _registrations.Add(name, definition);
            }

            _definitions.Add(definition);
        }
    }

    public bool Unregister(CommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (!_definitions.Remove(definition))
            {
                return false;
            }

            foreach (var name in new[] { definition.Name }.Concat(definition.Aliases))
            {
                if (_registrations.TryGetValue(name, out var registered) && ReferenceEquals(registered, definition))
                {
                    _registrations.Remove(name);
                }
            }

            return true;
        }
    }

    public bool TryResolve(string name, [NotNullWhen(true)] out CommandDefinition? definition)
    {
        lock (_gate)
        {
            return _registrations.TryGetValue(CommandLineParser.NormalizeName(name), out definition);
        }
    }

    public async ValueTask<CommandResult> ExecuteAsync(
        CommandContext context,
        CommandInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TryResolve(input.Name, out var definition))
        {
            return CommandResult.Failure($"Unknown command: /{input.Name}");
        }

        return await definition.Handler(context, input, cancellationToken).ConfigureAwait(false);
    }
}
