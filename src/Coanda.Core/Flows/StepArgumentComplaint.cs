namespace Coanda.Core.Flows;

/// <summary>
/// One way a system step's argument schema refuses a flow step's arguments: the argument it concerns,
/// when it concerns one (the missing one, for a missing required argument), and the complaint.
/// </summary>
public sealed record StepArgumentComplaint(string? Argument, string Complaint)
{
    public override string ToString() => Argument is null ? Complaint : $"argument '{Argument}' {Complaint}";
}
