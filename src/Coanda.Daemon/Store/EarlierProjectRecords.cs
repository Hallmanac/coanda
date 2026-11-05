using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Modules;
using Coanda.Core.Modules.Events;
using Coanda.Core.Projects;
using Coanda.Daemon.Projects;
using Coanda.Modules.Software;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;

namespace Coanda.Daemon.Store;

/// <summary>
/// Reads the project records a build from before repos and module settings wrote, and carries what they
/// hold that is now the software module's into that module's replicated settings. Such a build recorded,
/// in each project's registration, the remote of its one repository, that repository's primary branch,
/// and the build and test commands its registration named, and recorded the checks a merge waits for in
/// an event of its own; it snapshotted all of them into the project's document. This build reads the
/// registration's remote as the project's first repo (<see cref="Core.Projects.Events.ProjectRegistered"/>)
/// and no longer has the rest in its record, so the start carries them over once: it rewrites a
/// project document that holds no repos from the project's events, and gives each of the software
/// module's settings the earlier record holds a value for that value, as the daemon itself. A value an
/// earlier build recorded in the module's settings is replaced, since that build's steps read the
/// project's record and never the settings, so the record's value is the one its work ran with; a build
/// or test command the registration did not name is replaced by the default it ran. The settings are
/// carried once, which the carry's own events mark: a start after the first carries nothing again and
/// leaves a setting changed since as it is, and a project this build registered has nothing to carry.
/// The earlier records are read as the JSON the earlier build wrote, straight from the events table,
/// since this build has no types for them; nothing of them is changed.
/// </summary>
public sealed class EarlierProjectRecords(IDocumentStore store, NodeHome home, TimeProvider clock)
{
    private const string Query =
        """
        SELECT tenant_id, type, data FROM fi_events
        WHERE type IN ('project_registered', 'project_required_checks_set') AND is_archived = 0
        ORDER BY seq_id
        """;

    /// <summary>Carries every earlier project record the home holds; a home with none is left as it is.</summary>
    public async Task CarryAsync(CancellationToken cancellationToken)
    {
        foreach (Earlier earlier in Read(home.DatabasePath))
        {
            await CarryAsync(earlier, cancellationToken);
        }
    }

    private async Task CarryAsync(Earlier earlier, CancellationToken cancellationToken)
    {
        string tenant = earlier.Tenant;
        Project? project = await ProjectService.FindAsync(store, tenant, cancellationToken);
        if (project is null)
        {
            return;
        }

        await using IDocumentSession session = store.LightweightSession(tenant);
        bool changed = false;
        if (project.Repos.Count == 0)
        {
            project = await session.Events.AggregateStreamAsync<Project>(project.Id, token: cancellationToken)
                ?? throw new InvalidOperationException($"Project '{tenant}' has a document but no events.");
            session.Store(project);
            changed = true;
        }

        ModuleSettings? settings;
        await using (IQuerySession query = store.QuerySession(tenant))
        {
            IReadOnlyList<ModuleSettings> found = await query.Query<ModuleSettings>()
                .Where(s => s.Module == SoftwareSteps.Module)
                .ToListAsync(cancellationToken);
            settings = found.Count == 0 ? null : found[0];
        }

        // The settings are carried once: a stream holding an event the carry wrote has been carried, and
        // anything set since is the project's own choice.
        if (settings is not null
            && (await session.Events.FetchStreamAsync(settings.Id, token: cancellationToken)).Any(e => e.Data is ModuleSettingSet { SetBy: var by } && by == ProjectService.UpgradePrincipal.Id))
        {
            if (changed)
            {
                await session.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        Guid settingsId = settings?.Id ?? Ids.New();
        DateTimeOffset now = clock.GetUtcNow();
        List<ModuleSettingSet> carried = [];
        void Carry(string name, object? value)
        {
            if (value is not null)
            {
                carried.Add(new ModuleSettingSet(
                    settingsId, tenant, SoftwareSteps.Module, name, JsonSerializer.Serialize(value), ProjectService.UpgradePrincipal.Id, now));
            }
        }

        if (project.Repos.Count > 0)
        {
            Carry(SoftwareSettings.PrimaryBranch, new Dictionary<string, string> { [project.Repos[0].Name] = earlier.PrimaryBranch });
        }

        // A registration that named no command ran the default, which an inert value set beside it gives way to.
        Carry(SoftwareSettings.Build, earlier.BuildCommand ?? (settings?.ValueOf(SoftwareSettings.Build) is null ? null : SoftwareSettings.DefaultBuild));
        Carry(SoftwareSettings.Test, earlier.TestCommand ?? (settings?.ValueOf(SoftwareSettings.Test) is null ? null : SoftwareSettings.DefaultTest));
        if (earlier.Checks is { Count: > 0 } checks)
        {
            Carry(SoftwareSettings.RequiredChecks, checks);
        }

        if (carried.Count > 0)
        {
            if (settings is null)
            {
                session.Events.StartStream<ModuleSettings>(settingsId, [.. carried]);
            }
            else
            {
                session.Events.Append(settingsId, [.. carried]);
            }

            changed = true;
        }

        if (changed)
        {
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The earlier record of every project whose registration an earlier build wrote, which is one that
    /// names a primary branch, with the checks its latest earlier event named, if any.
    /// </summary>
    private static IReadOnlyList<Earlier> Read(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return [];
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = Query;

        var found = new Dictionary<string, Earlier>(StringComparer.Ordinal);
        var checks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string tenant = reader.GetString(0);
            using JsonDocument data = JsonDocument.Parse(reader.GetString(2));
            JsonElement root = data.RootElement;
            switch (reader.GetString(1))
            {
                case "project_registered" when Text(root, "primaryBranch") is { } branch:
                    found[tenant] = new Earlier(tenant, branch, Text(root, "buildCommand"), Text(root, "testCommand"));
                    break;
                case "project_required_checks_set" when root.TryGetProperty("checks", out JsonElement named) && named.ValueKind == JsonValueKind.Array:
                    checks[tenant] = [.. named.EnumerateArray().Select(c => c.GetString()).OfType<string>()];
                    break;
            }
        }

        return [.. found.Values.Select(e => e with { Checks = checks.GetValueOrDefault(e.Tenant) })];
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>What an earlier build recorded of one project that is now the software module's.</summary>
    private sealed record Earlier(string Tenant, string PrimaryBranch, string? BuildCommand, string? TestCommand)
    {
        public IReadOnlyList<string>? Checks { get; init; }
    }
}
