using System.Text.Json;
using NukiApiProbe;

// Probes the Nuki Web API, least to most invasive, to see which operations this account/token may perform:
//   1. Token check       GET    /account
//   2. List locks        GET    /smartlock
//   3. Read entries      GET    /smartlock/{id}/auth
//   4. Write test        PUT/POST/DELETE /smartlock/{id}/auth  (temporary keypad code, asks first, always cleans up)
//
// Usage: dotnet run [-- --lock <smartlockId>] [--read-only]

const string TestName = "API-TEST";
const string RenamedTestName = "API-TEST-RENAMED";
var pollTimeout = TimeSpan.FromSeconds(90);
var pollInterval = TimeSpan.FromSeconds(5);

long? lockArg = null;
bool readOnly = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--read-only") readOnly = true;
    else if (args[i] == "--lock" && i + 1 < args.Length && long.TryParse(args[++i], out long id)) lockArg = id;
}

var results = new List<(string Step, string Outcome, string Detail)>();

string? token = Secrets.GetToken();
if (token == null)
{
    Fail("No API token found.");
    Console.WriteLine("Set it once from the project folder with:");
    Console.WriteLine("  dotnet user-secrets set \"Nuki:ApiToken\" \"<your token>\"");
    Console.WriteLine("or set the NUKI_API_TOKEN environment variable.");
    return 2;
}

using var api = new NukiApi(token);

// ---------------------------------------------------------------- 1. Token check
Header("1. Token check (GET /account)");
var account = await api.GetAsync("account");
if (account.Ok)
{
    using var doc = JsonDocument.Parse(account.Body);
    var root = doc.RootElement;
    string accountType = Int(root, "type") switch { 0 => "user", 1 => "company", 2 => "caretaker", var t => $"type {t}" };
    Console.WriteLine($"  Account:      {Str(root, "name")} ({accountType})");
    Console.WriteLine($"  Subscription: {Str(root, "shsSubscriptionType") ?? "(none reported)"}");
    if (root.TryGetProperty("b2bActive", out var b2b)) Console.WriteLine($"  B2B active:   {b2b}");
    Record("1. Token check", account);
}
else
{
    Record("1. Token check", account);
    if (account.Status is 0 or 401)
        return Summary();
}

// ---------------------------------------------------------------- 2. List locks
Header("2. List locks (GET /smartlock)");
var lockList = await api.GetAsync("smartlock");
Record("2. List locks", lockList);
if (!lockList.Ok)
    return Summary();

var locks = new List<(long Id, string Name, int Type, int ServerState)>();
using (var doc = JsonDocument.Parse(lockList.Body))
{
    foreach (var el in doc.RootElement.EnumerateArray())
        locks.Add((el.GetProperty("smartlockId").GetInt64(), Str(el, "name") ?? "", Int(el, "type") ?? -1, Int(el, "serverState") ?? -1));
}

for (int i = 0; i < locks.Count; i++)
{
    var l = locks[i];
    Console.WriteLine($"  [{i + 1}] {l.Name}  smartlockId={l.Id} (hex {l.Id:X})  type={LockType(l.Type)}  server={ServerState(l.ServerState)}");
}
if (locks.Count == 0)
{
    Fail("The token can see no locks.");
    return Summary();
}

var target = locks[0];
if (lockArg != null)
{
    int match = locks.FindIndex(l => l.Id == lockArg);
    if (match < 0) { Fail($"--lock {lockArg} is not in the list above."); return Summary(); }
    target = locks[match];
}
else if (locks.Count > 1)
{
    Console.Write($"  Which lock should steps 3-4 use? [1-{locks.Count}]: ");
    if (!int.TryParse(Console.ReadLine(), out int pick) || pick < 1 || pick > locks.Count) { Fail("No lock selected."); return Summary(); }
    target = locks[pick - 1];
}
Console.WriteLine($"  Using: {target.Name} ({target.Id})");

// ---------------------------------------------------------------- 3. Read entries
Header($"3. Read entries (GET /smartlock/{target.Id}/auth)");
var auths = await api.GetAsync($"smartlock/{target.Id}/auth");
Record("3. Read entries", auths);
if (!auths.Ok)
    return Summary();

var existingCodes = new HashSet<int>();
var leftovers = new List<(string Id, string Name)>();
using (var doc = JsonDocument.Parse(auths.Body))
{
    var all = doc.RootElement.EnumerateArray().ToList();
    Console.WriteLine($"  {all.Count} authorization(s) on this lock:");
    foreach (var group in all.GroupBy(a => Int(a, "type") ?? -1).OrderBy(g => g.Key))
        Console.WriteLine($"    {AuthType(group.Key),-12} {group.Count()}");

    var keypadCodes = all.Where(a => Int(a, "type") == 13).ToList();
    if (keypadCodes.Count > 0)
    {
        Console.WriteLine("  Keypad codes (PINs hidden):");
        foreach (var a in keypadCodes)
        {
            Console.WriteLine($"    {Str(a, "name"),-32} enabled={Bool(a, "enabled")}  from={ShortDate(a, "allowedFromDate")}  until={ShortDate(a, "allowedUntilDate")}");
            if (Int(a, "code") is int c) existingCodes.Add(c);
            string name = Str(a, "name") ?? "";
            if (name.StartsWith(TestName, StringComparison.OrdinalIgnoreCase))
                leftovers.Add((Str(a, "id")!, name));
        }
    }
}

// ---------------------------------------------------------------- 4. Write test
Header("4. Write test (create, rename, delete a temporary keypad code)");
if (readOnly)
{
    results.Add(("4. Write test", "SKIP", "--read-only"));
    return Summary();
}

// Clean up anything an earlier, interrupted run left behind
foreach (var (id, name) in leftovers)
{
    if (!Confirm($"  Found leftover test entry '{name}'. Delete it first?")) continue;
    var del = await api.DeleteAsync($"smartlock/{target.Id}/auth/{id}");
    Console.WriteLine($"    DELETE -> {Status(del)}");
}

// Valid only for one day a year from now, so the code cannot open the door during the test.
// Dates are midnight at the lock (Panama, UTC-5, no daylight saving), which is how Nuki Web displays them.
var lockZone = TimeZoneInfo.FindSystemTimeZoneById("SA Pacific Standard Time");
DateTime fromLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, lockZone).Date.AddYears(1);
DateTime untilLocal = fromLocal.AddDays(1);
DateTime from = TimeZoneInfo.ConvertTimeToUtc(fromLocal, lockZone);
DateTime until = TimeZoneInfo.ConvertTimeToUtc(untilLocal, lockZone);
int pin = GeneratePin(existingCodes);

Console.WriteLine($"  This will create keypad code '{TestName}' on '{target.Name}' with PIN {pin},");
Console.WriteLine($"  valid only {fromLocal:yyyy-MM-dd} to {untilLocal:yyyy-MM-dd} (Panama time), then rename it and delete it.");
if (!Confirm("  Proceed?"))
{
    results.Add(("4. Write test", "SKIP", "Declined at prompt"));
    return Summary();
}

string? createdId = null;
bool deleted = false;
try
{
    // 4a. Create
    var create = await api.PutAsync($"smartlock/{target.Id}/auth", WithTimeWindow(new()
    {
        ["name"] = TestName,
        ["type"] = 13,
        ["code"] = pin,
        ["remoteAllowed"] = false
    }));
    Record("4a. Create", create);
    if (!create.Ok)
        return Summary();

    // Nuki applies writes asynchronously (204 = accepted), so confirm by reading back
    Console.WriteLine("  Waiting for the entry to appear on the lock...");
    createdId = await PollForEntry(e => Str(e, "name") == TestName && Int(e, "code") == pin);
    results.Add(("4b. Create confirmed", createdId != null ? "PASS" : "FAIL",
        createdId != null ? $"entry id {createdId}" : $"Accepted, but not visible after {pollTimeout.TotalSeconds:0}s"));
    if (createdId == null)
        return Summary();

    // Without a stored date window the PIN opens the door right now, so skip the rename and go to delete
    bool windowOk = await CheckTimeWindow(createdId, "4b2. Date limit applied");

    // 4c. Rename (update) - resends the window, because an update without it clears it
    string currentName = TestName;
    if (windowOk)
    {
        var update = await api.PostAsync($"smartlock/{target.Id}/auth/{createdId}",
            WithTimeWindow(new() { ["name"] = RenamedTestName }));
        Record("4c. Update", update);
        if (update.Ok)
        {
            Console.WriteLine("  Waiting for the rename to appear...");
            bool renamed = await PollForEntry(e => Str(e, "id") == createdId && Str(e, "name") == RenamedTestName) != null;
            results.Add(("4d. Update confirmed", renamed ? "PASS" : "FAIL", renamed ? "" : $"Not visible after {pollTimeout.TotalSeconds:0}s"));
            if (renamed)
            {
                currentName = RenamedTestName;
                windowOk = await CheckTimeWindow(createdId, "4d2. Date limit kept");
            }
        }
    }

    // Pause so the entry can be inspected in Nuki Web before it is removed
    Console.WriteLine($"\n  Keypad code '{currentName}' (PIN {pin}) is on '{target.Name}' now; you can check it in Nuki Web.");
    if (!windowOk)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  WARNING: no date limit is stored, so this PIN can open the door NOW. Deleting is the default.");
        Console.ResetColor();
    }
    if (!Confirm("  Delete it now?", defaultYes: !windowOk))
    {
        results.Add(("4e. Delete", "SKIP", "Kept at prompt; the next run offers to delete it"));
        return Summary();
    }

    // 4e. Delete
    var delete = await api.DeleteAsync($"smartlock/{target.Id}/auth/{createdId}");
    Record("4e. Delete", delete);
    if (delete.Ok)
    {
        Console.WriteLine("  Waiting for the entry to disappear...");
        deleted = await PollUntilGone(createdId);
        results.Add(("4f. Delete confirmed", deleted ? "PASS" : "FAIL", deleted ? "" : $"Still present after {pollTimeout.TotalSeconds:0}s"));
    }
}
finally
{
    if (createdId != null && !deleted)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  WARNING: test entry {createdId} may still be on the lock.");
        Console.WriteLine("  Run the probe again (it offers to delete leftovers) or remove it in the Nuki app.");
        Console.ResetColor();
    }
}

return Summary();

// ================================================================ helpers

// Nuki only enforces the date window when allowedWeekDays is sent with it (127 = every day)
Dictionary<string, object> WithTimeWindow(Dictionary<string, object> body)
{
    body["allowedFromDate"] = Iso(from);
    body["allowedUntilDate"] = Iso(until);
    body["allowedWeekDays"] = 127;
    return body;
}

// Reads the entry back and reports whether Nuki actually stored the date limit
async Task<bool> CheckTimeWindow(string id, string step)
{
    var r = await api.GetAsync($"smartlock/{target.Id}/auth?types=13");
    if (!r.Ok)
    {
        Record(step, r);
        return false;
    }
    using var doc = JsonDocument.Parse(r.Body);
    var entry = doc.RootElement.EnumerateArray().FirstOrDefault(e => Str(e, "id") == id);
    string? storedFrom = entry.ValueKind == JsonValueKind.Object ? Str(entry, "allowedFromDate") : null;
    string? storedUntil = entry.ValueKind == JsonValueKind.Object ? Str(entry, "allowedUntilDate") : null;
    int? storedDays = entry.ValueKind == JsonValueKind.Object ? Int(entry, "allowedWeekDays") : null;
    Console.WriteLine($"  Stored limit: from={storedFrom ?? "-"}  until={storedUntil ?? "-"}  weekDays={storedDays?.ToString() ?? "-"}");

    bool ok = storedFrom != null && storedUntil != null;
    results.Add((step, ok ? "PASS" : "FAIL", ok ? "" : "No date limit stored - the PIN is usable now"));
    return ok;
}

async Task<string?> PollForEntry(Func<JsonElement, bool> match)
{
    var deadline = DateTime.UtcNow + pollTimeout;
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(pollInterval);
        var r = await api.GetAsync($"smartlock/{target.Id}/auth?types=13");
        if (!r.Ok) continue;
        using var doc = JsonDocument.Parse(r.Body);
        foreach (var e in doc.RootElement.EnumerateArray())
            if (match(e)) return Str(e, "id");
    }
    return null;
}

async Task<bool> PollUntilGone(string id)
{
    var deadline = DateTime.UtcNow + pollTimeout;
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(pollInterval);
        var r = await api.GetAsync($"smartlock/{target.Id}/auth?types=13");
        if (!r.Ok) continue;
        using var doc = JsonDocument.Parse(r.Body);
        if (!doc.RootElement.EnumerateArray().Any(e => Str(e, "id") == id)) return true;
    }
    return false;
}

// Keypad codes: 6 digits, 1-9 only, may not start with "12"
static int GeneratePin(HashSet<int> taken)
{
    while (true)
    {
        var digits = Enumerable.Range(0, 6).Select(_ => (char)('0' + Random.Shared.Next(1, 10))).ToArray();
        string s = new(digits);
        if (s.StartsWith("12")) continue;
        int pin = int.Parse(s);
        if (!taken.Contains(pin)) return pin;
    }
}

void Record(string step, ApiResult r)
{
    string detail = r.Ok ? "" : $"{NukiApi.Explain(r.Status)} {Snippet(r.Body)}".Trim();
    results.Add((step, r.Ok ? "PASS" : "FAIL", $"HTTP {r.Status}{(detail == "" ? "" : " - " + detail)}"));
    Console.ForegroundColor = r.Ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"  {Status(r)}");
    Console.ResetColor();
    if (!r.Ok && detail != "") Console.WriteLine($"  {detail}");
}

int Summary()
{
    Header("Summary");
    foreach (var (step, outcome, detail) in results)
    {
        Console.ForegroundColor = outcome switch { "PASS" => ConsoleColor.Green, "FAIL" => ConsoleColor.Red, _ => ConsoleColor.Yellow };
        Console.Write($"  {outcome,-5}");
        Console.ResetColor();
        Console.WriteLine($" {step,-22} {detail}");
    }
    bool writeOk = results.Any(r => r.Step == "4f. Delete confirmed" && r.Outcome == "PASS")
                   && results.All(r => r.Outcome != "FAIL");
    Console.WriteLine();
    Console.WriteLine(writeOk
        ? "  Result: the API CAN now create, update and delete keypad codes on this lock."
        : "  Result: full keypad-code management through the API is NOT confirmed (see failures/skips above).");
    return results.Any(r => r.Outcome == "FAIL") ? 1 : 0;
}

static void Header(string text)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"\n== {text}");
    Console.ResetColor();
}

static void Fail(string text)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  {text}");
    Console.ResetColor();
}

static bool Confirm(string question, bool defaultYes = false)
{
    Console.Write($"{question} {(defaultYes ? "[Y/n]" : "[y/N]")}: ");
    string answer = Console.ReadLine()?.Trim() ?? "";
    return answer == "" ? defaultYes : answer.Equals("y", StringComparison.OrdinalIgnoreCase);
}

static string Status(ApiResult r) => r.Status == 0 ? "no response" : $"HTTP {r.Status}{(r.Ok ? " OK" : "")}";
static string Snippet(string body) => body.Length > 200 ? body[..200] + "..." : body;
static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

static string? Str(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
static int? Int(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
static bool? Bool(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
static string ShortDate(JsonElement e, string name) =>
    Str(e, name) is string s && DateTime.TryParse(s, out var d) ? d.ToString("yyyy-MM-dd") : "-";

static string LockType(int t) => t switch
{
    0 => "Smart Lock 1/2", 1 => "Box", 2 => "Opener", 3 => "Smart Door", 4 => "Smart Lock 3/4", 5 => "Smart Lock 5", _ => t.ToString()
};
static string ServerState(int s) => s switch
{
    0 => "ok", 1 => "unregistered", 2 => "auth uuid invalid", 3 => "auth invalid", 4 => "offline", _ => s.ToString()
};
static string AuthType(int t) => t switch
{
    0 => "app", 1 => "bridge", 2 => "fob", 3 => "keypad", 13 => "keypad code", 14 => "z-key", 15 => "virtual", _ => $"type {t}"
};
