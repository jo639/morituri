using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Sim.Serialization;

/// <summary>
/// 직렬화 봉투 (문서[1] 6장 = M4 뷰어·Phase 3 역사 DB의 공용 입력).
/// schemaVer는 "처음부터" 포함한다 — 영속 세계의 기록은 포맷이 진화해도 과거 데이터를 읽을 수 있어야 한다.
/// </summary>
public sealed record MatchRecord(
    int SchemaVer,
    ulong Seed,
    MatchResult Result,
    IReadOnlyList<SimEvent> Events);

/// <summary>
/// MatchRecord ↔ JSON. 결정론(원칙 B)의 보상: 같은 시드 = 같은 이벤트 스트림 = 같은 JSON.
/// 도메인(SimEvent)을 직렬화 속성으로 오염시키지 않기 위해 폴리모피즘을 계약 수정자로 격리한다.
/// </summary>
public static class MatchSerializer
{
    public const int SchemaVersion = 1;

    // SimEvent 9종 ↔ 타입 판별자. 문자열 판별자는 enum/타입 순서 변경에 견고하다(숫자 인덱스는 깨진다).
    private static readonly (Type Type, string Id)[] EventTypes =
    {
        (typeof(StateChanged),     "StateChanged"),
        (typeof(AttackSwung),      "AttackSwung"),
        (typeof(HitLanded),        "HitLanded"),
        (typeof(PoiseBroken),      "PoiseBroken"),
        (typeof(GuardBroken),      "GuardBroken"),
        (typeof(KnockedDown),      "KnockedDown"),
        (typeof(Decision),         "Decision"),
        (typeof(StaminaExhausted), "StaminaExhausted"),
        (typeof(MatchEnded),       "MatchEnded"),
    };

    private static readonly JsonSerializerOptions Options = BuildOptions();

    public static string Serialize(MatchRecord record) => JsonSerializer.Serialize(record, Options);

    public static MatchRecord Deserialize(string json) =>
        JsonSerializer.Deserialize<MatchRecord>(json, Options)
        ?? throw new JsonException("MatchRecord 역직렬화 결과가 null");

    /// <summary>한 경기를 실행하며 이벤트를 수집해 직렬화 가능한 레코드로 포착한다.</summary>
    public static MatchRecord Capture(FighterDef a, FighterDef b, ulong seed)
    {
        var events = new List<SimEvent>();
        var result = new MatchSim().Run(a, b, seed, events);
        return new MatchRecord(SchemaVersion, seed, result, events);
    }

    private static JsonSerializerOptions BuildOptions() => new()
    {
        // enum은 문자열로 — 숫자는 enum 재정렬 시 의미가 바뀐다(영속 기록엔 치명적).
        Converters = { new JsonStringEnumConverter() },
        // SimEvent 폴리모피즘을 도메인 속성 없이 런타임 계약으로 구성.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ConfigureSimEventPolymorphism } },
    };

    private static void ConfigureSimEventPolymorphism(JsonTypeInfo info)
    {
        if (info.Type != typeof(SimEvent)) return;

        var poly = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "type",
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
        };
        foreach (var (type, id) in EventTypes)
            poly.DerivedTypes.Add(new JsonDerivedType(type, id));
        info.PolymorphismOptions = poly;
    }
}
