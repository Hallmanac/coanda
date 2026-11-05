using System.Text.Json;
using Coanda.Core.Flows;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Sdk.Contract;
using Fisher;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// The one place a system step is dispatched, asked a dispatch's status, or told to stop one. The core's
/// own steps, and any a test registers beside them, are implemented here, one implementation per name;
/// every other step is a module's, named <c>&lt;module&gt;/&lt;step&gt;</c>, and is dispatched through
/// that module's host adapter when the module is switched on for the flow run's project and describes
/// the step. A flow run that started under an earlier flow version naming one of the five software steps
/// unprefixed dispatches that step to the software module (<see cref="EarlierRecords.CurrentStepName"/>). A step no
/// switched-on module provides pauses naming it, a done result its step's result schema refuses pauses
/// as a step that threw does, and a missing or ill-kinded argument of a core step pauses.
/// </summary>
public sealed class FlowSystemSteps
{
    private readonly Dictionary<string, IFlowSystemStep> systemSteps = new(StringComparer.Ordinal);
    private readonly HostedModules modules;
    private readonly IDocumentStore? store;

    /// <summary>
    /// The steps <paramref name="registered"/>, and the steps of <paramref name="modules"/>, which are
    /// dispatched in a project whose record, read from <paramref name="store"/>, switches their module on.
    /// With no modules or no store, only the registered steps are dispatched.
    /// </summary>
    public FlowSystemSteps(IEnumerable<IFlowSystemStep> registered, HostedModules? modules = null, IDocumentStore? store = null)
    {
        foreach (IFlowSystemStep systemStep in registered)
        {
            if (!systemSteps.TryAdd(systemStep.Name, systemStep))
            {
                throw new InvalidOperationException($"The system step '{systemStep.Name}' is registered twice; each name has one implementation.");
            }
        }

        this.modules = modules ?? new HostedModules(new Dictionary<string, ModuleHostAdapter>());
        this.store = store;
    }

    /// <summary>The names of the steps implemented here: the core's and any a test registered.</summary>
    public IReadOnlySet<string> Names => systemSteps.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The core's system steps, with the move-parcel step writing through <paramref name="parcels"/> and
    /// the timer reading <paramref name="clock"/>; derive-parcels is still a placeholder.
    /// </summary>
    public static IReadOnlyList<IFlowSystemStep> Core(ParcelService parcels, TimeProvider clock) =>
    [
        new MoveParcelSystemStep(parcels),
        new TimerSystemStep(clock),
        new WaitForHumanSystemStep(),
        new WaitForExternalEventSystemStep(),
        new EmitOutcomeSystemStep(),
        new PlaceholderSystemStep(CoreSystemSteps.DeriveParcels),
    ];

    /// <summary>
    /// The system step names a flow may name: the steps implemented here, and the steps described by the
    /// modules switched on for <paramref name="project"/>, or, for a definition that belongs to no
    /// project, by every module the daemon hosts.
    /// </summary>
    public async Task<IReadOnlySet<string>> NamesAsync(string? project, CancellationToken cancellationToken)
    {
        IEnumerable<string> switchedOn = project is null
            ? modules.Names
            : (await ProjectOfAsync(project, cancellationToken))?.Modules ?? [];
        return new HashSet<string>([.. systemSteps.Keys, .. modules.StepsOf(switchedOn)], StringComparer.Ordinal);
    }

    /// <summary>
    /// What the argument schema of each module step a flow of <paramref name="project"/>, or of no project
    /// when it is null, may name says of a step's arguments: the step's providing module is one switched on
    /// for the project, or, for no project, one the daemon hosts. A step implemented here, or one no such
    /// module provides, has no complaint, so the core's steps are checked as their names alone are.
    /// </summary>
    public async Task<StepArgumentCheck> ArgumentCheckAsync(string? project, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> switchedOn = project is null
            ? modules.Names.ToHashSet(StringComparer.Ordinal)
            : ((await ProjectOfAsync(project, cancellationToken))?.Modules ?? []).ToHashSet(StringComparer.Ordinal);
        return (name, arguments) =>
            !systemSteps.ContainsKey(name) && ModuleNames.PrefixOf(name) is { } module && switchedOn.Contains(module)
                ? modules.ArgumentComplaintsOf(module, name, arguments)
                : [];
    }

    /// <summary>
    /// What the argument schema of the step a stored flow names <paramref name="name"/> says of
    /// <paramref name="arguments"/>, as dispatch would read the name: one of the five steps the binary
    /// shipped unprefixed is the software module's. A step implemented here, or one no module the daemon
    /// hosts provides, has no complaint.
    /// </summary>
    public IReadOnlyList<StepArgumentComplaint> StoredArgumentComplaintsOf(string name, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (systemSteps.ContainsKey(name))
        {
            return [];
        }

        string canonical = EarlierRecords.CurrentStepName(name);
        return ModuleNames.PrefixOf(canonical) is { } module
            ? modules.ArgumentComplaintsOf(module, canonical, arguments)
            : [];
    }

    public async ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken)
    {
        string name = request.SystemStep;
        if (systemSteps.TryGetValue(name, out IFlowSystemStep? systemStep))
        {
            try
            {
                return await systemStep.DispatchAsync(request, cancellationToken);
            }
            catch (SystemStepArgumentException refused)
            {
                return new SystemStepAnswer.Paused(refused.Message, FlowRunLevers.PerformAgain(refused.Step));
            }
        }

        (ModuleHostAdapter? adapter, string? unprovided) = await ResolveAsync(request.Project, request.Step.Name, name, cancellationToken);
        if (adapter is null)
        {
            return new SystemStepAnswer.Paused(unprovided ?? string.Empty, FlowRunLevers.PerformAgain(request.Step.Name));
        }

        DispatchAnswer answer = await adapter.DispatchAsync(request.ToContract(), cancellationToken);
        return answer switch
        {
            DispatchAnswer.Done done => Done(request.Step, name, done.Result),
            DispatchAnswer.Paused paused => new SystemStepAnswer.Paused(paused.Reason, paused.Lever),
            DispatchAnswer.Running => new SystemStepAnswer.Running(),
            _ => throw new InvalidOperationException($"A module answered a dispatch in a way the daemon does not know: {answer.GetType().Name}."),
        };
    }

    /// <summary>
    /// Asks the system step <paramref name="name"/> of the flow step <paramref name="step"/> where its
    /// dispatch <paramref name="dispatchId"/> stands; a step no switched-on module provides pauses, as
    /// its dispatch would.
    /// </summary>
    public async ValueTask<SystemStepStatus> StatusAsync(string project, FlowStep step, string name, Guid dispatchId, CancellationToken cancellationToken)
    {
        if (systemSteps.TryGetValue(name, out IFlowSystemStep? systemStep))
        {
            return await systemStep.StatusAsync(dispatchId, cancellationToken);
        }

        (ModuleHostAdapter? adapter, string? unprovided) = await ResolveAsync(project, step.Name, name, cancellationToken);
        if (adapter is null)
        {
            return new SystemStepStatus.Paused(unprovided ?? string.Empty, FlowRunLevers.PerformAgain(step.Name));
        }

        StatusAnswer answer = await adapter.StatusAsync(new StatusRequest(dispatchId), cancellationToken);
        return answer switch
        {
            StatusAnswer.Running running => new SystemStepStatus.Running(running.Progress),
            StatusAnswer.Done done => Done(step, name, done.Result) switch
            {
                SystemStepAnswer.Done met => new SystemStepStatus.Done(met.Result),
                SystemStepAnswer.Paused refused => new SystemStepStatus.Paused(refused.Reason, refused.Lever),
                _ => throw new InvalidOperationException("A result is either met or refused."),
            },
            StatusAnswer.Paused paused => new SystemStepStatus.Paused(paused.Reason, paused.Lever),
            StatusAnswer.Unknown => new SystemStepStatus.Unknown(),
            _ => throw new InvalidOperationException($"A module answered a status in a way the daemon does not know: {answer.GetType().Name}."),
        };
    }

    /// <summary>
    /// Tells the system step <paramref name="name"/> to stop its dispatch <paramref name="dispatchId"/>;
    /// a step no module the daemon hosts provides has nothing running here to stop.
    /// </summary>
    public async ValueTask StopAsync(string name, Guid dispatchId, CancellationToken cancellationToken)
    {
        if (systemSteps.TryGetValue(name, out IFlowSystemStep? systemStep))
        {
            await systemStep.StopAsync(dispatchId, cancellationToken);
            return;
        }

        string step = EarlierRecords.CurrentStepName(name);
        if (ModuleNames.PrefixOf(step) is { } module && modules.Provides(module, step) && modules.AdapterOf(module) is { } adapter)
        {
            await adapter.StopAsync(new StopRequest(dispatchId), cancellationToken);
        }
    }

    /// <summary>
    /// The host adapter that dispatches the system step <paramref name="name"/> in
    /// <paramref name="project"/>, or the reason none does: the step's module is not switched on for
    /// the project, the daemon hosts no such module, or the module does not describe the step.
    /// </summary>
    private async Task<(ModuleHostAdapter? Adapter, string? Unprovided)> ResolveAsync(
        string project, string step, string name, CancellationToken cancellationToken)
    {
        string canonical = EarlierRecords.CurrentStepName(name);
        string unprovided = $"Step '{step}' names the system step '{name}', which no module switched on for project '{project}' provides";
        if (ModuleNames.PrefixOf(canonical) is not { } module)
        {
            return (null, $"{unprovided}; it names no module.");
        }

        Project? record = await ProjectOfAsync(project, cancellationToken);
        if (record is null || !record.IsSwitchedOn(module))
        {
            return (null, $"{unprovided}; it names module '{module}', which is not switched on for the project.");
        }

        if (modules.AdapterOf(module) is not { } adapter)
        {
            return (null, $"{unprovided}; it names module '{module}', which this node does not host.");
        }

        return modules.Provides(module, canonical)
            ? (adapter, null)
            : (null, $"{unprovided}; it names module '{module}', which describes no such step.");
    }

    /// <summary>
    /// A module's done result as the flow run records it, once it meets its step's result schema, or the
    /// pause a step that threw gets when it does not. An earlier flow version's open-pr step may route on
    /// a verdict open-pr no longer answers (<see cref="EarlierRecords.OpenPrResult"/>).
    /// </summary>
    private SystemStepAnswer Done(FlowStep step, string name, StepResult result)
    {
        string canonical = EarlierRecords.CurrentStepName(name);
        string module = ModuleNames.PrefixOf(canonical) ?? string.Empty;
        JsonElement document = JsonSerializer.SerializeToElement(result, ModuleJson.Options);
        IReadOnlyList<string> refusals = modules.RefusalsOf(module, canonical, document);
        if (refusals.Count > 0)
        {
            return new SystemStepAnswer.Paused(
                $"Step '{step.Name}' could not perform the system step '{name}': module '{module}' answered a result its step's result schema refuses: {string.Join("; ", refusals)}.",
                FlowRunLevers.PerformAgain(step.Name));
        }

        StepResult recorded = EarlierRecords.OpenPrResult(step, result);
        return ReferenceEquals(recorded, result)
            ? new SystemStepAnswer.Done(document)
            : new SystemStepAnswer.Done(JsonSerializer.SerializeToElement(recorded, ModuleJson.Options));
    }

    private async Task<Project?> ProjectOfAsync(string project, CancellationToken cancellationToken) =>
        store is null
            ? null
            : await ProjectService.FindAsync(store, Tenants.RequireProject(project), cancellationToken);
}
