using System.Collections;
using System.Globalization;
using System.Reflection;

namespace StandaloneSpectator;

/// <summary>Authoritative official spectator data only. No Unity calls or card inference.</summary>
public sealed class SpectatorState(long ownAccountId)
{
    sealed record Hand(bool Known, int Count, CardView[] Cards, string Reason);
    public sealed record CardView(int cardUid,int cardId,int purifyNum,bool isTemp,int battleCost);
    public sealed record PlayerView(string playerId,int slot,string? nick,int? heroId=null,int? hp=null,int? maxHp=null,int? gold=null,int? attack=null,int? defense=null);
    public bool HasAuthority=>authority;
    readonly Dictionary<long,Hand> hands=new();
    readonly Dictionary<long,PlayerView> players=new();
    long stagedRoom,room,focus;
    bool authority;
    string reason="Waiting for official spectator room";
    public long RoomId=>room!=0?room:stagedRoom;
    public bool IsReady=>authority&&hands.TryGetValue(focus,out var hand)&&hand.Known;
    public object Diagnostic()=>new{officialSpectating=authority,ready=IsReady,roomId=RoomId.ToString(CultureInfo.InvariantCulture),playerCount=players.Count,reason=IsReady?null:reason};
    public void Reset(string why){stagedRoom=room=focus=0;authority=false;hands.Clear();players.Clear();reason=why;}
    public void Focus(long id)
    {if(!authority||!players.ContainsKey(id))throw new InvalidOperationException("Unknown spectator player");focus=id;}
    public object Snapshot()
    {
        hands.TryGetValue(focus,out var hand);
        return new {mode=authority?"pve":"unavailable",officialSpectating=authority,status=authority?"ready":"unavailable",reason=authority?null:reason,
            sampledUtc=DateTime.UtcNow,roomId=room.ToString(CultureInfo.InvariantCulture),playerId=focus.ToString(CultureInfo.InvariantCulture),
            cards=IsReady?hand!.Cards:Array.Empty<CardView>(),handCount=hand?.Count,players=players.Values.OrderBy(p=>p.slot).Select(p=>{
                hands.TryGetValue(long.Parse(p.playerId,CultureInfo.InvariantCulture),out var h);
                return new{p.playerId,p.slot,p.nick,p.heroId,p.hp,p.maxHp,p.gold,p.attack,p.defense,
                    handKnown=authority&&h?.Known==true,handCount=h?.Count,handReason=h?.Reason,cards=authority&&h?.Known==true?h.Cards:Array.Empty<CardView>()};
            }).ToArray()};
    }
    public void Apply(short cmd,object data)
    {
        switch(cmd)
        {
            case 1001:case 1016:case 5196:Reset("Official spectator session ended");return;
            case 5192:
                Reset("Awaiting official spectator refresh");
                var map=Number(Get(data,"MapType"));if(map!=4&&map!=12)return;
                stagedRoom=Number(Get(data,"RoomId"));return;
            case 5194:
                var declared=Number(Get(data,"RoomId"));var full=Get(data,"Room");
                if(full==null||stagedRoom==0||(declared!=0&&declared!=stagedRoom)||Number(Get(full,"Id"))!=stagedRoom){Reset("Spectator room mismatch");return;}
                AcceptRoom(full,true);return;
            case 5008:case 1003:
                if(!authority)return;
                var sync=Get(data,"Room");if(sync==null){Reset("Room snapshot missing");return;}AcceptRoom(sync,false);return;
            case 1109:
                if(authority)Replace(Number(Get(data,"PlayerId")),List(data,"Cards"));return;
            case 1040:
                if(!authority)return;
                foreach(var effect in List(data,"EffectDatas"))
                {
                    var id=Number(Get(effect,"PlayerId"));var kind=Get(effect,"DataCase")?.ToString();
                    if(kind=="Card") {var change=Get(effect,"Card");if(change==null)Invalidate(id,0);else Replace(id,List(change,"Cards"));}
                    else if(kind=="ConvertCard") {var convert=Get(effect,"ConvertCard");if(convert==null)Invalidate(id,0);else ConvertHand(id,List(convert,"ConvertCards"));}
                    if(players.TryGetValue(id,out var player))
                    {
                        var update=Get(effect,kind??"");
                        if(update!=null)players[id]=kind switch {
                            "Hp"=>player with{hp=Optional(update,"CurrHp"),maxHp=Optional(update,"MaxHp")},
                            "Gold"=>player with{gold=Optional(update,"CurrGold")},
                            "Atk"=>player with{attack=Optional(update,"CurrAtk")},
                            "Def"=>player with{defense=Optional(update,"CurrDef")}, _=>player};
                    }
                }
                return;
        }
    }
    void AcceptRoom(object full,bool initial)
    {
        long id=Number(Get(full,"Id")),map=Number(Get(full,"MapType")),roomState=Number(Get(full,"State"));
        var roster=List(full,"Players");var viewers=List(full,"WatchPlayers");
        if(id==0||id!=(initial?stagedRoom:room)||roomState!=25||(map!=4&&map!=12)
            ||!viewers.Any(p=>Number(Get(p,"Id"))==ownAccountId)||roster.Any(p=>Number(Get(p,"Id"))==ownAccountId))
        {Reset("Room no longer confirms official PVE spectator authority");return;}
        authority=true;room=id;stagedRoom=0;hands.Clear();players.Clear();
        foreach(var player in roster)
        {
            long pid=Number(Get(player,"Id"));if(pid<=0||players.ContainsKey(pid)){Reset("Invalid player roster");return;}
            players[pid]=new(pid.ToString(CultureInfo.InvariantCulture),(int)Number(Get(player,"Slot")),Get(player,"Nick") as string);
            var hero=Get(player,"Hero");
            if(hero==null)Invalidate(pid,0);
            else{players[pid]=players[pid] with{heroId=Optional(hero,"HeroId"),hp=Optional(hero,"Hp"),maxHp=Optional(hero,"MaxHp"),gold=Optional(hero,"Gold"),attack=Optional(hero,"Attack"),defense=Optional(hero,"Defense")};Replace(pid,List(hero,"Cards"));}
        }
        if(players.Count==0){Reset("Spectator roster empty");return;}
        if(!players.ContainsKey(focus))focus=players.Values.OrderBy(p=>p.slot).Select(p=>long.Parse(p.playerId,CultureInfo.InvariantCulture)).First();
    }
    void Replace(long id,object[] values)
    {
        if(!players.ContainsKey(id))return;
        var cards=ParseCards(values);if(cards==null){Invalidate(id,values.Length);return;}
        hands[id]=new(true,values.Length,cards,"authoritative");
    }
    void ConvertHand(long id,object[] patches)
    {
        if(!players.ContainsKey(id))return;
        if(!hands.TryGetValue(id,out var hand)||!hand.Known){Invalidate(id,hand?.Count??0);return;}
        var updates=ParseCards(patches);if(updates==null||updates.Any(c=>!hand.Cards.Any(old=>old.cardUid==c.cardUid))) {Invalidate(id,hand.Count);return;}
        var byUid=updates.ToDictionary(c=>c.cardUid);
        hands[id]=new(true,hand.Count,hand.Cards.Select(c=>byUid.GetValueOrDefault(c.cardUid,c)).ToArray(),"authoritative");
    }
    void Invalidate(long id,int count){if(players.ContainsKey(id))hands[id]=new(false,count,Array.Empty<CardView>(),"server_masked_or_incomplete");reason="Focused hand is masked or not authoritative";}
    static CardView[]? ParseCards(object[] values)
    {
        var cards=new List<CardView>();var seen=new HashSet<int>();
        foreach(var card in values)
        {
            int uid=(int)Number(Get(card,"UniqueId")),id=(int)Number(Get(card,"CardId"));
            if(uid<=0||id<=0||!seen.Add(uid))return null;
            cards.Add(new(uid,id,(int)Number(Get(card,"PurifyNum")),Get(card,"IsTemp") is true,(int)Number(Get(card,"BattleCost"))));
        }
        return cards.ToArray();
    }
    static object? Get(object value,string key)=>value.GetType().GetProperty(key,BindingFlags.Public|BindingFlags.Instance)?.GetValue(value)
        ??value.GetType().GetField(key,BindingFlags.Public|BindingFlags.Instance)?.GetValue(value);
    static object[] List(object value,string key)
    {if(Get(value,key)is not IEnumerable list)throw new InvalidDataException("Missing authoritative list "+key);return list.Cast<object>().ToArray();}
    static int? Optional(object value,string key){var field=Get(value,key);return field==null?null:(int)Number(field);}
    static long Number(object? value)=>value==null?0:System.Convert.ToInt64(value,CultureInfo.InvariantCulture);
}
