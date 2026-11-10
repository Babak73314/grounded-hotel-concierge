using GroundedConcierge.Answering;
using GroundedConcierge.Domain;

// A scripted conversation with the fictional hotel's concierge. No network, no
// key: the "model" is a ScriptedDraftModel that returns canned drafts, so the
// output is identical on every run and every machine. The date is pinned for
// the same reason.

Console.OutputEncoding = System.Text.Encoding.UTF8;

var clock = new FixedHotelClock(new DateOnly(2026, 11, 16));
var catalogue = new InMemoryHotelCatalogSource();

// A staff override, as a property-management system would hold it: the
// Bosphorus Suite is closed for maintenance on the first four nights. The
// earliest-stay search below has to walk past them.
for (var night = 0; night < 4; night++)
    catalogue.SetOverride(new HotelDailyOverride("BOSSUITE", clock.Today.AddDays(night), null, 0, "maintenance"));

var hotel = new HotelDataService(catalogue);

// The drafts a real model might write for the policy questions below. The first
// one claims an action the concierge never performs, so the gate refuses it and
// asks once more.
var model = new ScriptedDraftModel(
    "Breakfast is served from 07:00 to 10:30, and I have reserved a table for you. [breakfast]",
    "Breakfast is served in the garden restaurant from 07:00 to 10:30 and is included in every rate. [breakfast]",
    "Evet, küçük evcil hayvanlar talep üzerine Bahçe Odalarında kabul edilir; lütfen gelmeden önce resepsiyona bildirin. [pets]");

var engine = new ConciergeEngine(
    hotel, clock, new InMemoryPolicyPassages(), model,
    new HotelAnswerGate(new HotelFactValidator()),
    contactEmail: "reception@example.invalid");

var english = new HotelConversationState("en");
var turkish = new HotelConversationState("tr");

var turns = new (HotelConversationState State, string Text)[]
{
    (english, "How much is a room from 4 to 7 December for 2 adults?"),
    (english, "What about the Executive room?"),
    (english, "Find the earliest Bosphorus Suite for 3 nights for 2 adults."),
    (english, "What time is breakfast?"),
    (english, "Do you have a helipad?"),
    (turkish, "18-20 Aralık arası Deluxe oda ne kadar?"),
    (turkish, "Evcil hayvan kabul ediyor musunuz?"),
};

Console.WriteLine($"Today at the property: {clock.Today:dd.MM.yyyy}");

foreach (var (state, text) in turns)
{
    var callsBefore = model.Calls;
    var reply = await engine.AnswerAsync(text, state);

    Console.WriteLine();
    Console.WriteLine($"Guest: {text}");
    Console.WriteLine($"[{reply.Kind} · model calls this turn: {model.Calls - callsBefore}"
        + (reply.WasRegenerated ? " · first draft refused" : string.Empty)
        + (reply.WasWithheld ? " · withheld" : string.Empty) + "]");
    Console.WriteLine(reply.Text);
}
