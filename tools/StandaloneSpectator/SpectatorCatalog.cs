using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StandaloneSpectator;

public sealed class SpectatorCatalog
{
    readonly Dictionary<int,(string Name,string Description,int Cost)> cards=new();
    readonly Dictionary<int,string> heroes=new();
    public SpectatorCatalog(string protocolPath,string dataPath)
    {
        using var p=new ProtocolMessages(protocolPath);
        Dictionary<int,string> Local(string file,string type)
        {
            var result=new Dictionary<int,string>();var message=p.Parse(type,File.ReadAllBytes(Path.Combine(dataPath,file)));
            foreach(var item in ProtocolMessages.Enumerable(message,"Locals"))
                result[N(ProtocolMessages.Get(item,"Id"))]=Clean(ProtocolMessages.Get(item,"Simplified") as string??"");
            return result;
        }
        var texts=Local("STRCard.bin","STRCardConfigure");
        foreach(var c in ProtocolMessages.Enumerable(p.Parse("CardConfigure",File.ReadAllBytes(Path.Combine(dataPath,"Card.bin"))),"Infos"))
        {
            int id=N(ProtocolMessages.Get(c,"Id"));
            cards[id]=(texts.GetValueOrDefault(N(ProtocolMessages.Get(c,"NameID")),"卡牌 "+id),texts.GetValueOrDefault(N(ProtocolMessages.Get(c,"DescId")),""),N(ProtocolMessages.Get(c,"Cost")));
        }
        var names=Local("STRCharacter.bin","STRCharacterConfigure");
        foreach(var h in ProtocolMessages.Enumerable(p.Parse("CharacterConfigure",File.ReadAllBytes(Path.Combine(dataPath,"Character.bin"))),"Infos"))
        {int id=N(ProtocolMessages.Get(h,"Id"));heroes[id]=names.GetValueOrDefault(N(ProtocolMessages.Get(h,"NameID")),"角色 "+id);}
    }
    public JsonNode Enrich(object snapshot)
    {
        var node=JsonSerializer.SerializeToNode(snapshot)!;
        void Cards(JsonNode? list)
        {
            if(list is not JsonArray items)return;
            foreach(var card in items)
            {
                if(card==null)continue;int id=card["cardId"]!.GetValue<int>();
                if(cards.TryGetValue(id,out var info))
                {card["name"]=info.Name;card["description"]=info.Description;card["baseCost"]=info.Cost;if(card["battleCost"]!.GetValue<int>()<0)card["battleCost"]=info.Cost;}
            }
        }
        Cards(node["cards"]);
        if(node["players"] is JsonArray players)foreach(var player in players)
        {if(player==null)continue;Cards(player["cards"]);if(player["heroId"] is JsonValue value&&value.TryGetValue<int>(out int id)&&heroes.TryGetValue(id,out var name))player["heroName"]=name;}
        return node;
    }
    static int N(object? v)=>v==null?0:Convert.ToInt32(v,CultureInfo.InvariantCulture);
    static string Clean(string text)=>System.Net.WebUtility.HtmlDecode(Regex.Replace(text,"<[^>]*>",""));
}
