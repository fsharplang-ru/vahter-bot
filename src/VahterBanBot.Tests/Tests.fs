module Tests

open System
open System.Net.Http
open System.Text
open Telegram.Bot.Types
open VahterBanBot.Tests.ContainerTestBase
open Xunit
open Xunit.Extensions.AssemblyFixture

type Tests(containers: VahterTestContainers) =
    [<Fact>]
    let ``Random path returns OK`` () = task {
        let! resp = containers.Http.GetAsync("/" + Guid.NewGuid().ToString())
        let! body = resp.Content.ReadAsStringAsync()
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode)
        Assert.Equal("OK", body)
    }

    [<Fact>]
    let ``Not possible to interact with the bot without authorization`` () = task {
        let http = new HttpClient()
        let content = new StringContent("""{"update_id":123}""", Encoding.UTF8, "application/json")
        let uri = containers.Uri.ToString() + "bot"
        let! resp = http.PostAsync(uri, content)
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode)
    }
    
    [<Fact>]
    let ``Should be possible to interact with the bot`` () = task {
        let! resp = Update(Id = 123) |>  containers.SendMessage
        let! body = resp.Content.ReadAsStringAsync()
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode)
        Assert.Equal("null", body)
    }

    interface IAssemblyFixture<VahterTestContainers>
