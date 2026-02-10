module BaseTests

open System
open System.Net.Http
open System.Text
open Telegram.Bot.Types
open VahterBanBot.Tests.ContainerTestBase
open Xunit

type BaseTests(fixture: MlDisabledVahterTestContainers) =
    [<Fact>]
    let ``Random path returns OK`` () = task {
        let! resp = fixture.Http.GetAsync("/" + Guid.NewGuid().ToString())
        let! body = resp.Content.ReadAsStringAsync()
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode)
        Assert.Equal("OK", body)
    }

    [<Fact>]
    let ``Not possible to interact with the bot without authorization`` () = task {
        let http = new HttpClient()
        let content = new StringContent("""{"update_id":123}""", Encoding.UTF8, "application/json")
        let uri = fixture.Uri.ToString() + "bot"
        let! resp = http.PostAsync(uri, content)
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode)
    }
    
    [<Fact>]
    let ``Should be possible to interact with the bot`` () = task {
        let! resp = Update(Id = 123) |>  fixture.SendMessage
        let! body = resp.Content.ReadAsStringAsync()
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode)
        Assert.Equal("null", body)
    }

