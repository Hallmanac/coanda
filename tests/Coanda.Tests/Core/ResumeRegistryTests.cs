using Coanda.Core;
using Coanda.Core.Registry;

namespace Coanda.Tests.Core;

/// <summary>
/// A person's resume stays open to people only, so neither a module nor an agent step's session resumes a flow
/// run, and the resume the trigger loop writes for an external event is the daemon's own Agent-role entry.
/// </summary>
public sealed class ResumeRegistryTests
{
    private static readonly Principal Session = Principal.ForSession(new SessionBinding("alpha", Ids.New(), Ids.New()));
    private static readonly Principal Module = Principal.ForModule("software");

    [Fact]
    public void A_module_and_a_session_cannot_resume_a_flow_run()
    {
        Assert.Equal("'flow-run.resume' is not open to the Agent role.", Assert.Throws<DomainException>(() => CommandRegistry.Require("flow-run.resume", Session)).Message);
        Assert.Equal("'flow-run.resume' is not open to the Module role.", Assert.Throws<DomainException>(() => CommandRegistry.Require("flow-run.resume", Module)).Message);
        Assert.Equal("flow-run.resume", CommandRegistry.Require("flow-run.resume", Principal.LocalOwner).Name);
    }

    [Fact]
    public void The_resume_for_an_external_event_is_open_to_the_agent_role_only()
    {
        RegistryEntry entry = CommandRegistry.Require("flow-run.resume-on-event", new Principal("external-event:x", Role.Agent));

        Assert.Equal([Role.Agent], entry.AllowedRoles);
        Assert.Throws<DomainException>(() => CommandRegistry.Require("flow-run.resume-on-event", Module));
        Assert.Throws<DomainException>(() => CommandRegistry.Require("flow-run.resume-on-event", Principal.LocalOwner));
    }
}
