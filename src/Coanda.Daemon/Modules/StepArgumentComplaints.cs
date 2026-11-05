using Coanda.Core.Flows;
using NJsonSchema.Validation;

namespace Coanda.Daemon.Modules;

/// <summary>
/// A failure of a step's argument schema in words a flow author reads: the argument it concerns, when it
/// concerns one (the missing one, for a missing required argument; the undeclared one, for an argument
/// the schema does not declare), and what is wrong, with the schema's own name for the rule.
/// </summary>
public static class StepArgumentComplaints
{
    public static StepArgumentComplaint Of(ValidationError failure)
    {
        string? argument = string.IsNullOrEmpty(failure.Property) ? null : failure.Property;
        string complaint = failure.Kind switch
        {
            ValidationErrorKind.PropertyRequired => "is required and not given",
            ValidationErrorKind.NoAdditionalPropertiesAllowed => "is not one the schema declares",
            ValidationErrorKind.StringExpected => "must be a string",
            ValidationErrorKind.NumberExpected => "must be a number",
            ValidationErrorKind.IntegerExpected => "must be an integer",
            ValidationErrorKind.BooleanExpected => "must be a boolean",
            _ => "breaks a rule of the schema",
        };
        return argument is null
            ? new StepArgumentComplaint(null, $"the arguments break a rule of the schema ({failure.Kind} at {failure.Path ?? "#"})")
            : new StepArgumentComplaint(argument, $"{complaint} ({failure.Kind})");
    }
}
