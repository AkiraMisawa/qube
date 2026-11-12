using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace Qube.Composition.Tests;

// Who the page is for, as the header's account menu names them (ADR-0017 §4): the user's id, and their desk where
// configuration names one (C32). Until ME-ID sign-in (C30) the user is the host's Qube:DemoUser.
public sealed class AccountApiTests(QubeFactory factory) : IClassFixture<QubeFactory>
{
    [Fact] // ADR-0017 §4: the account menu names the user, and their desk where configuration names one
    public async Task The_page_reads_who_it_is_for()
    {
        var mine = (await factory.CreateClient().GetFromJsonAsync<JsonObject>("/api/me"))!;
        using var other = factory.WithWebHostBuilder(b => b.UseSetting("Qube:DemoUser", "ben").UseSetting("Qube:Desks:ben", "rates"));
        var bens = (await other.CreateClient().GetFromJsonAsync<JsonObject>("/api/me"))!;

        Assert.Equal(("akira", null), (mine["id"]!.GetValue<string>(), mine["desk"]?.GetValue<string>()));
        Assert.Equal(("ben", "rates"), (bens["id"]!.GetValue<string>(), bens["desk"]?.GetValue<string>()));
    }
}
