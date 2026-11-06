using System.Reflection;
using Coanda.Daemon.Store;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

/// <summary>
/// No connection the store opens is ever handed to a second opener while the first still holds it, even in
/// the instant Microsoft.Data.Sqlite takes to hand it over.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft.Data.Sqlite 10.0.9 pools the native connections behind a connection string. Handing one out,
/// <c>SqliteConnectionFactory.GetConnection</c> takes it from the pool under the pool's lock and then, outside
/// that lock, calls <c>SqliteConnectionInternal.Activate</c>, which marks it active and only then records the
/// connection that holds it. A connection marked active with no holder recorded is what the pool takes for
/// one whose holder was collected while open, and <c>SqliteConnectionPool.GetConnection</c> reclaims every
/// such connection, under its lock, whenever an open finds no idle connection and the pool holds an even
/// number. Reclaiming in that instant puts the connection back in the pool while its new holder is about to
/// use it, and the open that reclaimed it takes it at once, so two holders now share one native connection.
/// </para>
/// <para>
/// Sharing it is what the full suite showed. The second holder's open registers the store's SQL function
/// again, which SQLite refuses while the first holder's statement is still stepping (Error 5, "unable to
/// delete/modify user-function due to active statements"). And each Fisher session's close rolls back
/// whatever transaction its connection carries, which on a shared connection is the other holder's, whose
/// commit then fails ("This SqliteTransaction has completed").
/// </para>
/// <para>
/// The tests stand in for the race by putting a held connection in the state <c>Activate</c> leaves it in
/// for that instant, then opening another connection with the store's connection string.
/// </para>
/// </remarks>
public sealed class StoreConnectionTests
{
    /// <summary>The name of the three-argument function Fisher's data source registers on every connection it opens.</summary>
    private const string StoreFunction = "fi_vector_distance";

    [Fact]
    public void An_open_registering_the_stores_function_is_not_refused_by_a_statement_stepping_on_a_connection_being_handed_over()
    {
        using var temp = new TemporaryHome();
        string connectionString = StoreConfiguration.ConnectionStringOf(temp.Home.DatabasePath);
        using SqliteConnection first = OpenAsTheStoreDoes(connectionString);
        using SqliteConnection second = OpenAsTheStoreDoes(connectionString);
        using SqliteCommand stepping = first.CreateCommand();
        stepping.CommandText = "select 1 union all select 2";
        SqliteDataReader reader = stepping.ExecuteReader();
        Assert.True(reader.Read());

        Exception? refused;
        bool secondRowRead;
        using (HandingOver.Begin(first))
        {
            using var third = new SqliteConnection(connectionString);
            third.Open();
            refused = Record.Exception(() => RegisterTheStoresFunction(third));
            secondRowRead = reader.Read();
            reader.Dispose();
        }

        // an open of the store's never shares a connection whose statement is still stepping
        Assert.Null(refused);
        Assert.True(secondRowRead);
    }

    [Fact]
    public void The_rollback_a_closing_session_makes_does_not_reach_a_transaction_on_a_connection_being_handed_over()
    {
        using var temp = new TemporaryHome();
        string connectionString = StoreConfiguration.ConnectionStringOf(temp.Home.DatabasePath);
        using SqliteConnection first = OpenAsTheStoreDoes(connectionString);
        using SqliteConnection second = OpenAsTheStoreDoes(connectionString);
        Execute(first, "create table written (value integer)");
        SqliteTransaction transaction = first.BeginTransaction();
        Execute(first, "insert into written values (1)", transaction);

        using (HandingOver.Begin(first))
        {
            using var third = new SqliteConnection(connectionString);
            third.Open();

            // what Fisher's session close runs on its connection, whatever that connection carries
            Exception? nothingToRollBack = Record.Exception(() => Execute(third, "rollback"));
            Assert.True(nothingToRollBack is null or SqliteException, $"The rollback threw {nothingToRollBack}.");
        }

        // the transaction is the first holder's alone to end
        Exception? commit = Record.Exception(transaction.Commit);
        transaction.Dispose();
        Assert.Null(commit);
        Assert.Equal(1L, Scalar(first, "select count(*) from written"));
    }

    private static SqliteConnection OpenAsTheStoreDoes(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        RegisterTheStoresFunction(connection);
        return connection;
    }

    private static void RegisterTheStoresFunction(SqliteConnection connection) =>
        connection.CreateFunction<object?, object?, object?, double?>(StoreFunction, (_, _, _) => null, isDeterministic: true);

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>
    /// Holds an open connection in the state <c>SqliteConnectionInternal.Activate</c> leaves it in between its
    /// two writes, marked active with no holder recorded, and records its holder again once disposed.
    /// </summary>
    private sealed class HandingOver : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly SqliteConnection holder;
        private readonly WeakReference<SqliteConnection> recorded;

        private HandingOver(SqliteConnection holder, WeakReference<SqliteConnection> recorded)
        {
            this.holder = holder;
            this.recorded = recorded;
        }

        public static HandingOver Begin(SqliteConnection holder)
        {
            object inner = typeof(SqliteConnection).GetField("_innerConnection", Private)?.GetValue(holder)
                ?? throw new InvalidOperationException("The connection holds no native connection.");
            Type innerType = inner.GetType();
            var recorded = (WeakReference<SqliteConnection>)(innerType.GetField("_outerConnection", Private)?.GetValue(inner)
                ?? throw new InvalidOperationException("Microsoft.Data.Sqlite no longer records a connection's holder in _outerConnection."));
            FieldInfo active = innerType.GetField("_active", Private)
                ?? throw new InvalidOperationException("Microsoft.Data.Sqlite no longer marks a connection active in _active.");
            Assert.Equal(true, active.GetValue(inner));
            recorded.SetTarget(null!);
            return new HandingOver(holder, recorded);
        }

        public void Dispose() => recorded.SetTarget(holder);
    }
}
