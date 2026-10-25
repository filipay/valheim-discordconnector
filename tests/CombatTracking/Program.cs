using DiscordConnector.Records;

using Tracker = DiscordConnector.Records.CombatCorrelation<string>;

int passed = 0;
void Test(string name, Action test)
{
    test();
    Console.WriteLine($"PASS {name}");
    passed++;
}
void Equal(int expected, int actual)
{
    if (expected != actual) throw new Exception($"Expected {expected}, got {actual}");
}
CombatSnapshot S(float hp = 10, string victim = "victim", long owner = 1, bool eligible = true,
    string world = "world") => new(world, victim, owner, hp, eligible, DateTime.UtcNow);
Tracker.Invocation Damage(Tracker t, string victim = "victim", string player = "account") =>
    t.BeginDamage(S(victim: victim), "attacker", player, "name");
void Death(Tracker t, string victim = "victim")
{
    var d = t.BeginDeath(S(0, victim));
    t.DeathCompleted(d, S(0, victim), "prefab/biome");
    Equal(0, t.End(d, true).Count);
}

Test("death inside damage waits for successful damage finalizer", () =>
{
    var t = new Tracker();
    var hit = Damage(t);
    Death(t);
    t.DamageApplied(hit, S(0));
    var kills = t.End(hit, true);
    Equal(1, kills.Count);
    if (kills[0].Id != "world:victim" || kills[0].PlayerId != "account") throw new Exception("Wrong identity");
});
Test("death after damage requires its successful finalizer", () =>
{
    var t = new Tracker();
    var hit = Damage(t);
    t.DamageApplied(hit, S(0));
    Equal(0, t.End(hit, true).Count);
    var d = t.BeginDeath(S(0));
    t.DeathCompleted(d, S(0), "death");
    Equal(1, t.End(d, true).Count);
});
Test("nested same victim credits only innermost hit", () =>
{
    var t = new Tracker();
    var outer = Damage(t, player: "outer");
    var inner = Damage(t, player: "inner");
    Death(t);
    t.DamageApplied(inner, S(0));
    Equal(0, t.End(inner, true).Count);
    t.DamageApplied(outer, S(0));
    var kills = t.End(outer, true);
    Equal(1, kills.Count);
    if (kills[0].PlayerId != "inner") throw new Exception("Outer inherited nested kill");
});
foreach (bool success in new[] { true, false })
    Test($"nested different victims wait for enclosing invocation ({success})", () =>
    {
        var t = new Tracker();
        var outer = Damage(t, "outer");
        var inner = Damage(t);
        Death(t);
        t.DamageApplied(inner, S(0));
        Equal(0, t.End(inner, true).Count);
        t.DamageApplied(outer, S(5, "outer"));
        Equal(success ? 1 : 0, t.End(outer, success).Count);
    });
Test("nonlethal then environmental lethal never inherits player", () =>
{
    var t = new Tracker();
    var hit = Damage(t);
    t.DamageApplied(hit, S(5));
    Equal(0, t.End(hit, true).Count);
    var env = Damage(t, player: "");
    Death(t);
    t.DamageApplied(env, S(0));
    Equal(0, t.End(env, true).Count);
});
foreach (bool damageFailure in new[] { true, false })
    Test($"exceptions invalidate attribution (damage={damageFailure})", () =>
    {
        var t = new Tracker();
        var hit = Damage(t);
        var d = t.BeginDeath(S(0));
        t.DeathCompleted(d, S(0), "death");
        Equal(0, t.End(d, damageFailure).Count);
        t.DamageApplied(hit, S(0));
        Equal(0, t.End(hit, !damageFailure).Count);
        var again = t.BeginDeath(S(0));
        t.DeathCompleted(again, S(0), "death");
        Equal(0, t.End(again, true).Count);
    });
foreach (string invalidation in new[] { "new hit", "revival", "reset", "ownership" })
    Test($"deferred candidate invalidated by {invalidation}", () =>
    {
        var t = new Tracker();
        var hit = Damage(t);
        t.DamageApplied(hit, S(0));
        Equal(0, t.End(hit, true).Count);
        if (invalidation == "new hit")
        {
            var next = t.BeginDamage(S(0), "", "", "");
            Equal(0, t.End(next, true).Count);
        }
        else if (invalidation == "reset") t.Clear();
        else t.Invalidate("world", "victim");
        var d = t.BeginDeath(S(0));
        t.DeathCompleted(d, S(0), "death");
        Equal(0, t.End(d, true).Count);
    });
foreach (var bad in new[] { S(owner: 0), S(eligible: false), S(victim: ""), S(world: ""), S(0), S(float.NaN), S(float.PositiveInfinity) })
    Test($"invalid snapshot rejected ({bad.WorldId}/{bad.VictimId}/{bad.Owner}/{bad.Health}/{bad.Eligible})", () =>
    {
        var t = new Tracker();
        var hit = t.BeginDamage(bad, "attacker", "account", "name");
        Death(t);
        t.DamageApplied(hit, S(0));
        Equal(0, t.End(hit, true).Count);
    });
Test("ownership transfer at death rejects deferred attribution", () =>
{
    var t = new Tracker();
    var hit = Damage(t);
    t.DamageApplied(hit, S(0));
    Equal(0, t.End(hit, true).Count);
    var d = t.BeginDeath(S(0, owner: 2));
    t.DeathCompleted(d, S(0, owner: 2), "death");
    Equal(0, t.End(d, true).Count);
});
Test("revival in death callback rejects attribution", () =>
{
    var t = new Tracker();
    var hit = Damage(t);
    var d = t.BeginDeath(S(0));
    t.DeathCompleted(d, S(1), "death");
    Equal(0, t.End(d, true).Count);
    t.DamageApplied(hit, S(0));
    Equal(0, t.End(hit, true).Count);
});
Test("missing postfix (skipped original) cannot confirm damage or death", () =>
{
    var t = new Tracker();
    var hit = Damage(t);
    Death(t);
    Equal(0, t.End(hit, true).Count);
});
Test("duplicate death callback consumed once", () =>
{
    var t = new Tracker();
    var hit = Damage(t);
    Death(t);
    t.DamageApplied(hit, S(0));
    Equal(1, t.End(hit, true).Count);
    var d = t.BeginDeath(S(0));
    t.DeathCompleted(d, S(0), "again");
    Equal(0, t.End(d, true).Count);
});
Test("damage inside death waits for enclosing death success", () =>
{
    var t = new Tracker();
    var outer = t.BeginDeath(S(0, "outer"));
    var hit = Damage(t);
    Death(t);
    t.DamageApplied(hit, S(0));
    Equal(0, t.End(hit, true).Count);
    t.DeathCompleted(outer, S(0, "outer"), "outer death");
    Equal(0, t.End(outer, false).Count);
});
Test("completed death supplies zero-HP evidence if damage destroyed its view", () =>
{
    var t = new Tracker();
    var hit = Damage(t);
    Death(t);
    t.DamageApplied(hit, null);
    Equal(1, t.End(hit, true).Count);
});
Test("missing damage after-snapshot without confirmed death is rejected", () =>
{
    var t = new Tracker();
    var hit = Damage(t);
    t.DamageApplied(hit, null);
    Equal(0, t.End(hit, true).Count);
});
Test("deferred animation up to a minute accepted; older candidate expires", () =>
{
    foreach (int seconds in new[] { 30, 61 })
    {
        var t = new Tracker();
        var now = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        var hit = t.BeginDamage(new("world", "victim", 1, 10, true, now), "attacker", "account", "name");
        t.DamageApplied(hit, new("world", "victim", 1, 0, true, now));
        Equal(0, t.End(hit, true).Count);
        var dead = new CombatSnapshot("world", "victim", 1, 0, true, now.AddSeconds(seconds));
        var d = t.BeginDeath(dead);
        t.DeathCompleted(d, dead, "death");
        Equal(seconds == 30 ? 1 : 0, t.End(d, true).Count);
    }
});
Test("nested unattributable hit supersedes outer player", () =>
{
    var t = new Tracker();
    var outer = Damage(t);
    var inner = Damage(t, player: "");
    Death(t);
    t.DamageApplied(inner, S(0));
    Equal(0, t.End(inner, true).Count);
    t.DamageApplied(outer, S(0));
    Equal(0, t.End(outer, true).Count);
});
Test("skipped enclosing original cannot release nested attribution", () =>
{
    var t = new Tracker();
    var outer = Damage(t, "outer");
    var inner = Damage(t);
    Death(t);
    t.DamageApplied(inner, S(0));
    Equal(0, t.End(inner, true).Count);
    Equal(0, t.End(outer, true).Count);
});
Test("postzero environmental hit invalidates deferred candidate conservatively", () =>
{
    var t = new Tracker();
    var hit = Damage(t);
    t.DamageApplied(hit, S(0));
    Equal(0, t.End(hit, true).Count);
    var env = t.BeginDamage(S(0), "", "", "");
    t.DamageApplied(env, S(0));
    Equal(0, t.End(env, true).Count);
    var d = t.BeginDeath(S(0));
    t.DeathCompleted(d, S(0), "death");
    Equal(0, t.End(d, true).Count);
});
Console.WriteLine($"{passed} correlation checks passed");
Integration.Run();
