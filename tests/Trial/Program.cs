using ValoAutoPick;

if (!Edition.IsTrial) throw new Exception("Trial compilation was not enabled");
if (Edition.DisplayName != "SpikeSide 体験版") throw new Exception("Trial branding is incorrect");
var defaultAgent = Guid.NewGuid().ToString();
var mapAgent = Guid.NewGuid().ToString();
var settings = new Settings { AgentId = defaultAgent, MapAgents = new() { ["Ascent"] = mapAgent } };
var api = new TestApi();
await new PickEngine(api, _ => { }).Tick(settings, DateTimeOffset.UtcNow, default);
if (api.Selected != defaultAgent || api.Locks != 1) throw new Exception("Trial used map override");
settings.Normalize();
if (settings.MapAgents.Count != 0) throw new Exception("Trial retained map settings");
if (!Storage.SettingsPath.EndsWith("quickpickerpro-trial-settings.json")) throw new Exception("Trial reused full edition settings");
Console.WriteLine("PASS trial edition ignores map overrides and isolates settings");

sealed class TestApi : IGameApi
{
    public string Subject => "me";
    public string Selected = "";
    public int Locks;
    public Task<string?> GetMatchId(CancellationToken ct) => Task.FromResult<string?>("match");
    public Task<string?> GetCoreMatchId(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> GetCoreMapId(string id, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> GetPartyState(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> GetLocalGameFlowState(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<MatchSnapshot> GetMatch(string id, CancellationToken ct) => Task.FromResult(new MatchSnapshot(id, "character_select_active", [new("me", Selected, Locks > 0 ? "locked" : "")], "/Game/Maps/Ascent/Ascent"));
    public Task Select(string id, string agent, CancellationToken ct) { Selected = agent; return Task.CompletedTask; }
    public Task Lock(string id, string agent, CancellationToken ct) { Locks++; return Task.CompletedTask; }
}
