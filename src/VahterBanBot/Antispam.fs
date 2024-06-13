module VahterBanBot.Antispam

open System
open System.Linq

let cyrillicLikeCharacters = [| 'u'; 't'; 'a' |]
let cyrillicCharacters = "абвгдежзиклмнопрстуфхцчшщъыьэюяё".ToHashSet()
    
let countFakeCyrillicWords (wl: string list) =
    
    let hasCyrillicLikeCharacters (w: string) =
        w.IndexOfAny(cyrillicLikeCharacters) <> -1
    
    let isMostlyCyrillic (w: string) =
        let isCyrillic c = cyrillicCharacters.Contains(c)
        
        w.Count(isCyrillic) > (w.Length / 2)
        
    let isFakeCyrillicWord w =
        isMostlyCyrillic w && hasCyrillicLikeCharacters w
        
    wl.Count(isFakeCyrillicWord)
    
let phrases = [
    10, [ "обучение"; "бесплатное" ]
    10, [ "бесплатное"; "обучение" ]
    7, [ "удаленная"; "работа" ]
    7, [ "удаленную"; "работу" ]
    3, [ "в"; "лс" ]
    3, [ "в"; "личку" ]
    3, [ "в"; "личные"; "сообщения" ]
]

let countPhrases (wl: string list) =
    // premium performance
    let rec countPhrase wl totalScore phrase =
        let score, p::ps = phrase

        match wl with
        | w :: ws when w = p ->
            if ws.Take(ps.Length).SequenceEqual(ps) then
                countPhrase (List.skip ps.Length ws) (totalScore + score) phrase
            else
                countPhrase ws totalScore phrase
        | _ -> totalScore
                
    List.sumBy (countPhrase wl 0) phrases

let wordPrefixesWeighted = [
    10, "крипт"
    10, "crypto"
    10, "defi"
    10, "usdt"
    10, "трейд"
    7, "вакансия"
    5, "партнер"
    5, "заработок"
    5, "заработк"
    3, "зарплата"
]

let countWords (wl: string list) =
    let checkWord wl word =
        let score, (actualWord: string) = word
        
        let checkSingleWord (w: string) = if w.StartsWith(actualWord) then score else 0
        
        List.sumBy checkSingleWord wl
        
    List.sumBy (checkWord wl) wordPrefixesWeighted

let distillWords (str: string) =
    // regexs are probably better
    let isCyrLatAlphaChar c =
        let isLat = c >= 'a' && c <= 'z'
        let isCyr = c >= 'а' && c <= 'я' // who cares about Ё
        let isDigit = c >= '0' && c <= '9'
        let isDollar = c = '$' // useful
        
        isLat || isCyr || isDigit || isDollar
    
    let filteredStr = String.filter isCyrLatAlphaChar (str.ToLower())
    
    List.ofArray <| filteredStr.Split(' ', StringSplitOptions.TrimEntries ||| StringSplitOptions.RemoveEmptyEntries)
    
let countEmojiLikeCharacters str =
    let mutable emojis = 0
    
    let countEmoji (c: char) =
        if c >= char 0xDD00 then emojis <- emojis + 1
    
    String.iter countEmoji str
    
    emojis
    
let calcSpamScore msg =
    let words = distillWords msg
    
    (countFakeCyrillicWords words) * 100
        + (countEmojiLikeCharacters msg) * 5
        + (countPhrases words) * 10
        + (countWords words) * 10