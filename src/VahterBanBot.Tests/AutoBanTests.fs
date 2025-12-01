module VahterBanBot.Tests.AutoBanTests

open VahterBanBot.Tests.ContainerTestBase
open VahterBanBot.Tests.TgMessageUtils
open Xunit
open Xunit.Extensions.AssemblyFixture

type BanTests(fixture: MlDisabledVahterTestContainers) =

    [<Fact>]
    let ``Messages without strange symbols won't be autodeleted`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        let! _ = fixture.SendMessage msgUpdate
        
        // assert that the message not banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.False msgBanned
    }
    
    [<Fact>]
    let ``Messages WITH strange symbols will be autodeleted even without ML`` () = task {
        // record a message
        let msgUpdate = Tg.quickMsg(chat = fixture.ChatsToMonitor[0])
        // adding BRAILLE PATTERN BLANK (U+2800) as a strange symbol
        msgUpdate.Message.Text <- msgUpdate.Message.Text + '\u2800'.ToString()
        
        let! _ = fixture.SendMessage msgUpdate
        
        // assert that the message not banned
        let! msgBanned = fixture.MessageBanned msgUpdate.Message
        Assert.True msgBanned
    }
    
    interface IAssemblyFixture<MlDisabledVahterTestContainers>
