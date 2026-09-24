using Keel.Domain.Rules;

namespace Keel.Application.Rules;

/// <summary>A rule was refused because <see cref="RuleValidator"/> reported errors; nothing was written.</summary>
public sealed class RuleValidationException : InvalidOperationException
{
    /// <summary>Creates the exception for the validator's problems.</summary>
    public RuleValidationException(IReadOnlyList<RuleProblem> problems)
        : base("The rule is not valid: " + string.Join(" ", (problems ?? []).Where(p => p.Severity == RuleProblemSeverity.Error).Select(p => p.Message)))
    {
        Problems = problems ?? [];
    }

    /// <summary>Creates the exception with no problems listed.</summary>
    public RuleValidationException()
        : this([])
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public RuleValidationException(string message)
        : base(message)
    {
        Problems = [];
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public RuleValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [];
    }

    /// <summary>Everything the validator reported (errors and warnings).</summary>
    public IReadOnlyList<RuleProblem> Problems { get; }
}
