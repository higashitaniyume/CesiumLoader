using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace AutoThanksMod.IntegrationTests;

public sealed class RuntimeMetadataContractTests
{
    [Fact]
    public void RealGameAssemblyDeclaresAllSettlementAndLikeMembersWithMatchingSignatures()
    {
        // Inspect the real PE metadata without loading it or creating conflicting runtime types.
        string path = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "RuntimeAssemblyPath").Value;
        Assert.True(File.Exists(path), "Real game assembly is required for this local contract test: " + path);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var names = new TypeNames();

        TypeDefinition Type(string fullName) => reader.TypeDefinitions.Select(reader.GetTypeDefinition)
            .Single(x => reader.GetString(x.Namespace) + "." + reader.GetString(x.Name) == fullName);
        FieldDefinition Field(string owner, string fieldName, string expectedType)
        {
            var field = Type(owner).GetFields().Select(reader.GetFieldDefinition)
                .Single(x => reader.GetString(x.Name) == fieldName);
            Assert.Equal(expectedType, field.DecodeSignature(names, (object)null));
            return field;
        }
        void Method(string owner, string methodName, string result, params string[] parameters)
        {
            var method = Type(owner).GetMethods().Select(reader.GetMethodDefinition)
                .Single(x => reader.GetString(x.Name) == methodName &&
                    x.DecodeSignature(names, (object)null).ParameterTypes.SequenceEqual(parameters));
            var signature = method.DecodeSignature(names, (object)null);
            Assert.Equal(result, signature.ReturnType);
            Assert.Equal(parameters, signature.ParameterTypes.ToArray());
            Assert.Equal(MethodAttributes.Public, method.Attributes & MethodAttributes.MemberAccessMask);
        }

        var settlement = Field("UI.BattleSettlementPanel", "_BattleSettlement", "GameLogic.BattleSettlement");
        Assert.Equal(FieldAttributes.Private, settlement.Attributes & FieldAttributes.FieldAccessMask);
        Field("GameLogic.BattleSettlement", "WinTeamId", "Int64");
        Method("GameLogic.BattleSettlement", "get_IsPVP", "Boolean");
        Method("GameLogic.BattleSettlement", "GetPlayerSettlement", "GameLogic.PlayerSettlement", "Int32");
        Method("GameLogic.BattleSettlement", "GetPlayerSettlementById", "GameLogic.PlayerSettlement", "Int64");
        Method("GameLogic.BattleLogic", "GetPlayerDataByTeamId", "System.Collections.Generic.List`1<GameLogic.BattlePlayerData>", "Int64");
        Field("GameLogic.PlayerSettlement", "PlayerData", "GameLogic.BattlePlayerData");
        Field("GameLogic.BattlePlayerData", "player", "GameLogic.RoomPlayer");
        Method("GameLogic.BattlePlayerData", "get_characterType", "CharacterType");
        Method("GameLogic.RoomPlayer", "get_Id", "Int64");
        Method("GameLogic.RoomPlayer", "get_IsBot", "Boolean");
        Field("UI.UIBattleSettlementPanel", "step", "FairyGUI.Controller");
        Field("UI.UIBattleSettlementPanel", "com_Winner", "UI.UIBattleSettlement_Com_ShowWiner");
        Field("UI.UIBattleSettlementPanel", "com_ShowTime", "UI.UIBattleSettlement_Com_ShowTime");
        foreach (string row in new[] { "com_Player1", "com_Player2", "com_Player3", "com_Player4" })
            Field("UI.UIBattleSettlement_Com_ShowTime", row, "UI.UIBattleSettlement_Com_Item");
        Field("UI.UIBattleSettlement_Com_Item", "btn_Praise", "UI.UIBattleSettlement_Button_OperatePlayer");
        Field("UI.UIBattleSettlement_Button_OperatePlayer", "isFinish", "FairyGUI.Controller");
        Field("UI.UIBattleSettlement_Button_OperatePlayer", "Cut_in", "FairyGUI.Transition");
        Field("UI.UIBattleSettlement_Com_ShowWiner", "btn_Praise", "UI.UIBattleSettlement_Button_Praise");
        Field("UI.UIBattleSettlement_Button_Praise", "Cut_in", "FairyGUI.Transition");
        Method("FairyGUI.GObject", "get_onStage", "Boolean");
        Method("FairyGUI.GObject", "get_visible", "Boolean");
        Method("FairyGUI.GObject", "get_grayed", "Boolean");
        Method("FairyGUI.GObject", "get_onClick", "FairyGUI.EventListener");
        Method("FairyGUI.Controller", "get_selectedIndex", "Int32");
        Method("FairyGUI.Transition", "get_playing", "Boolean");
        Method("FairyGUI.EventListener", "Call", "Boolean");
        Method("FairyGUI.EventListener", "Retain", "Void");
        Method("FairyGUI.EventListener", "get_isEmpty", "Boolean");
    }

    private sealed class TypeNames : ISignatureTypeProvider<string, object>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "function-pointer";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            string ns = reader.GetString(type.Namespace);
            return (ns.Length == 0 ? "" : ns + ".") + reader.GetString(type.Name);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            string ns = reader.GetString(type.Namespace);
            return (ns.Length == 0 ? "" : ns + ".") + reader.GetString(type.Name);
        }
        public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
