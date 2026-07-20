using Morituri.Sim.Core;
using Morituri.Sim.Data;

namespace Morituri.Headless;

/// <summary>
/// [13] 스토리·캠페인 + 살아있는 세계 — 전부 메타층(Sim 무접촉, 매트릭스 무관).
///  - 캠페인: 마일스톤 트리거형 이벤트 체인(서막 S0·S5 → 1막 비트 → 종막 의식). 기존 텍스트 이벤트 파이프 재사용.
///    stage: prologue(서막) → act1(1막) → chronicle(캠페인 종료 = 영속 샌드박스). 구세이브(stage 없음) = chronicle.
///  - 반란 지수(Unrest 0~100): 시즌 틱 사이클(평온→소문→폭동→검문→소강). 효과는 비용·기회·톤만 — 진행 차단 없음.
///  - 전설(Legends): 창세 시드 4명 + 명전 승격. 카토가 닮은 현역을 참조(시즌 2회 쿨다운).
///  - 카토 코멘터리: 매 경기 후 한 줄 평(내 경기 100% · AI전 35%) — MatchSummary.Cato.
/// </summary>
public sealed partial class Game
{
    // ── 스토리 상태 (world.json 영속) ──
    private string _storyStage = "chronicle";               // prologue → act1 → chronicle
    private readonly HashSet<string> _storyBeats = new();   // 스폰된 비트 id (s0, s5, house_*, b2~b5, finale)
    private string? _storyCtx;                              // 현재 대기 스토리 이벤트의 문맥(가문 비트 = ludusId)
    private string? _fixChoice;                             // 조작 제안의 선택(accept/refuse) — 무레나 대사 변주
    private readonly HashSet<string> _storyFlags = new();   // [13a] 선택의 꼬리 — 이후 씬의 분기·문구·에토스에만 사용(게임플레이 스탯 신규 0)
    private readonly List<KeepsakeRec> _keepsakes = new();  // 가이우스의 유령 — 보관함 유품(유서·메모·서신·단서)
    private float _unrest;                                  // 반란 지수 0~100
    private readonly List<LegendRec> _legends = new();      // 전설 카탈로그
    private int _legendRefs;                                // 이번 시즌 카토 전설 참조 횟수(≤2)
    private bool _promotedFlag;                             // 이번 시즌 내 모리튜리 승격(종막 게이트, 시즌 내 한정)
    private int _favorAtE1;                                 // E1 발화 시점의 총애 — E2 게이트("E1 후 특명 완수") 기준점

    private sealed record LegendRec(string Name, string Epithet, string Weapon, string Personality,
        string Record, string Fate, int Auc, string Source);   // Source: seed(창세) / hof(명전 승격)
    /// <summary>보관함 문서 — 유서·메모·서신·증서·단서. 클릭 열람용 타입 있는 유품(구 유품함 단서를 승격).</summary>
    private sealed record KeepsakeRec(string Type, string Title, string Body, string From, string When);
    private sealed record CampaignDoc(string Stage, string[] Beats, string? Hint, string[]? Clues = null,
        string Voice = "cato");   // 경기평의 화자 — 카토를 내치면 "thea"(내 경기)·"lucilius"(AI전)로 갈린다
    private sealed record UnrestDoc(int Level, string Stage, string Icon, string Effects);

    // ── 신규 세계 / 구세이브 ──

    /// <summary>새 세계의 스토리 초기화 — 서막 개시(장례 S0). CLI(playerless)는 스토리 없음.</summary>
    private void InitStoryNewWorld()
    {
        if (_playerless) { _storyStage = "chronicle"; return; }
        _storyStage = "prologue";
        SeedLegends();
        SpawnStory("story_s0", "s0");
    }

    /// <summary>캠페인 생략(뉴게임 옵션) — 각본 없이 시작, 전부 해금.</summary>
    public string SkipCampaignJson()
    {
        _storyStage = "chronicle";
        _storyBeats.Add("skipped");
        if (_pendingEventId is { } id && id.StartsWith("story_")) { _pendingEventId = null; _pendingEventFighter = null; }
        _story.Add((0, "story", "{ludus} 각본 없는 시작 — 모래가 곧 이야기다"));
        SaveWorld();
        return StateJson();
    }

    // ── 마일스톤 트리거 채널 (확률 아님 — 조건 충족 시 확정 발화, 랜덤 이벤트보다 우선) ──

    /// <summary>다음 스토리 씬/비트를 스폰. 서막 씬은 언제든, 1막 비트는 내 경기 페이싱(afterMatch).
    /// 캠페인 종료(chronicle) 후엔 후일담 「황제의 게임」 아크(E1~E3)만 — 종막(finale)을 본 커리어 한정.</summary>
    private bool MaybeSpawnStoryEvent(bool afterMatch)
    {
        if (_playerless || _pendingEventId != null) return false;
        if (_storyStage == "chronicle") return MaybeSpawnEmperorArc(afterMatch);

        // 서막 「유산」 — 장례(S0) → 빈 막사(S1) → [첫 영입] → 첫 훈련(S3) → 의무실(S4) → 개막 전야(S5)
        // v0.3: 무레나는 서막에 오지 않는다. 서막의 압박은 얼굴이 아니라 숫자(장부)가 담당 — 첫 방문은 1막 A3.
        if (!_storyBeats.Contains("s0")) return SpawnStory("story_s0", "s0");
        if (!_storyBeats.Contains("s1")) return SpawnStory("story_s1", "s1");
        if (!_storyBeats.Contains("s2")) return SpawnStory("story_s2", "s2");
        bool hired = _cast.Any(g => g.IsPlayer);
        if (!_storyBeats.Contains("s3") && hired) return SpawnStory("story_s3", "s3");
        if (!_storyBeats.Contains("s4") && hired) return SpawnStory("story_s4", "s4");
        if (!_storyBeats.Contains("s5") && hired) return SpawnStory("story_s5", "s5");
        if (_storyStage == "prologue")
        {
            if (!SeasonActive) return false;
            _storyStage = "act1";   // 개막과 함께 1막 「모래 위의 도시」
        }
        if (!afterMatch || !SeasonActive) return false;

        // ── 1막 「빚」 — 무레나는 두 번 온다: 채권자로 먼저(A3), 유혹자로 나중에(A5) ──
        // A0 개막 — 판돈을 정리하는 자들이 위쪽 세 줄에 앉아 있다(무레나의 예고)
        if (!_storyBeats.Contains("a0")) return SpawnStory("story_a0", "a0");
        // A2 세 가문 — 각자의 방식으로 신참을 "환영" (개성 타입 바인딩 — 어느 가문이 뽑혀도 동작).
        // 리그에 같은 개성의 루두스가 여럿이어도 환영은 개성당 한 번뿐이다 — 같은 서신이 두 번 오면 세계가 얇아 보인다.
        foreach (var r in ActiveRivalLudi)
            if (!_storyBeats.Contains("house_" + r.Id) && !_storyFlags.Contains("house_p_" + r.Persona))
            {
                Flag("house_p_" + r.Persona);
                return SpawnStory("story_house_" + r.Persona, "house_" + r.Id, ctx: r.Id);
            }
        // A3 첫 상환일 — 무레나 첫 등장. 조작 제안 없음(돈만). 최소 3경기를 치른 뒤에 온다.
        if (!_storyBeats.Contains("a3") && MyMatchesPlayed >= 3) return SpawnStory("story_a3", "a3");
        if (!_storyBeats.Contains("a3")) return false;   // A3 전에는 1막이 더 진행되지 않는다(페이싱 보장)
        // A4 관중의 맛 — 조영관 루킬리우스
        if (!_storyBeats.Contains("a4")) return SpawnStory("story_a4", "a4");
        // A5 두 번째 방문 — 조작 최초 제안(승부조작·사채 해금). 그가 파는 건 두 번째부터다.
        if (!_storyBeats.Contains("a5")) return SpawnStory("story_a5", "a5");
        // A5′ 답을 받으러 온 날 — 값을 물은 자에게만(궁금해하는 것도 값이 붙는다)
        if (!_storyBeats.Contains("a5b") && _storyFlags.Contains("asked_price")
            && !_storyFlags.Contains("fixed_once") && !_storyFlags.Contains("refused_fix"))
            return SpawnStory("story_a5b", "a5b");
        // A6 검을 닦지 않은 밤 — 조작의 꼬리(수락했을 때만). 선택은 반드시 사람의 얼굴로 돌아온다.
        if (!_storyBeats.Contains("a6") && _storyFlags.Contains("fixed_once")) return SpawnStory("story_a6", "a6");
        // A7 이름이 불렸다 — 값이 매겨지기 시작한다(B5·C1 예고)
        if (!_storyBeats.Contains("a7") && LudusTier() >= 1) return SpawnStory("story_a7", "a7");
        // ── 2막 「대가」 — 값이 청구되기 시작한다. 그리고 끝에서 카토가 무너진다 ──
        // B1 첫 피 — 리액티브(내 선수가 처음 부상을 안고 돌아온 날). 테아가 값을 센다.
        if (!_storyBeats.Contains("bl") && _cast.Any(g => g.IsPlayer && g.InjuryMatches > 0))
            return SpawnStory("story_b_blood", "bl");
        // ② 첫 원한 — 지목 격파 도전장
        if (!_storyBeats.Contains("b2") && _cast.Any(g => !g.IsPlayer) && _cast.Any(g => g.IsPlayer))
            return SpawnStory("story_challenge", "b2", ctx: ChallengeTarget()?.Id);
        // B3 피로도가 낳은 괴물 — 지친 몸으로 이긴 날. 테아가 처음으로 화를 낸다(B7 복선).
        if (!_storyBeats.Contains("mo") && _cast.Any(g => g.IsPlayer && g.Fatigue >= 80 && g.CW > 0))
            return SpawnStory("story_b_monster", "mo");
        // ③ 시대의 소음 — 반란 지수 점화
        if (!_storyBeats.Contains("b3")) return SpawnStory("story_unrest", "b3");
        // B5 담장 아래 — 규율로 눌렀거나 침묵했을 때만 온다(S3·A6의 회수)
        if (!_storyBeats.Contains("wa")
            && (_storyFlags.Contains("cato_sided") || _storyFlags.Contains("stayed_silent")))
            return SpawnStory("story_b_wall", "wa");
        // B6 루킬리우스의 발주 — 처형전
        if (!_storyBeats.Contains("ex")) return SpawnStory("story_b_exec", "ex");
        // B7 「20년」 — 2막의 정점. 시즌 1의 2/3 지점에 확정 발화.
        if (!_storyBeats.Contains("cf") && SeasonProgress >= 0.66f) return SpawnStory("story_b_confess", "cf");
        if (!_storyBeats.Contains("cf")) return false;   // 자백 전에는 3막으로 넘어가지 않는다
        // B8 무레나가 안다는 것 — 자백 직후
        if (!_storyBeats.Contains("mk")) return SpawnStory("story_b_murena", "mk");
        // ── 3막 「승격」 — 아버지와 똑같은 갈림길. 다만 이번엔 이기면 무슨 일이 일어나는지 안다 ──
        // C0 검은 인장의 시험대 — 한 번도 팔지 않은 자에게만. 깨끗한 길에는 보호막이 없다.
        if (!_storyBeats.Contains("tr") && !_storyFlags.Contains("fixed_once"))
            return SpawnStory("story_c_trial", "tr");
        // C1 값이 매겨진 밤 — 간판을 사겠다는 제안
        if (!_storyBeats.Contains("of") && _cast.Count(g => g.IsPlayer) >= 2)
            return SpawnStory("story_c_offer", "of");
        // ⑤ 승격 결전 전야 — 검은 인장의 마지막 요구 (다음 내 경기가 남아 있을 때)
        if (!_storyBeats.Contains("b5") && _cast.Any(g => g.IsPlayer) && HasMyMatchAheadNow())
            return SpawnStory("story_showdown", "b5");
        return false;
    }

    /// <summary>후일담 「황제의 게임」 — 명성 사다리 후반에 예약된 서사(긴장 인계 장치 §6-3).
    /// 독립 게이트: 1막 비트(b4·b5) 이수와 무관, 단 종막(finale)을 본 커리어만(각본 없이 시작·구세이브 제외).
    /// E1 명문 루두스(4단계) → E2 특명 완수(또는 총애 상승·컵 우승) → E3 콜로세움의 지배자(6단계) 또는 컵 우승.</summary>
    private bool MaybeSpawnEmperorArc(bool afterMatch)
    {
        if (!afterMatch || !SeasonActive || !_storyBeats.Contains("finale")) return false;
        if (!_storyBeats.Contains("e1"))
        {
            if (LudusTier() < 3) return false;
            _favorAtE1 = _favor;   // "E1 이후의 특명 완수"를 재기 위한 기준점
            return SpawnStory("story_e1", "e1");
        }
        if (!_storyBeats.Contains("e2"))
        {
            if (_favor > _favorAtE1 || _edictDone || _myCupTitles > 0) return SpawnStory("story_e2", "e2");
            return false;
        }
        if (!_storyBeats.Contains("e3"))
        {
            if (LudusTier() >= 5 || _myCupTitles > 0) return SpawnStory("story_e3", "e3");
            return false;
        }
        return false;
    }

    // ── [13a] 선택의 꼬리 · 에토스 · 단서(기억의 벽) ──

    /// <summary>씬 선택의 흔적. 이후 씬의 분기·문구에만 쓴다 — 전투·스탯 무관.</summary>
    private string Flag(string f) { _storyFlags.Add(f); return ""; }

    /// <summary>기억의 벽 조각(7개) — 획득 시 카드 UI를 띄우지 않는다(글자만 선명해짐).</summary>
    private static readonly string[] ClueIds =
        { "clue_axe", "clue_ledger", "clue_commentary", "clue_legend", "clue_thea", "clue_letter", "clue_recall" };
    private int ClueCount => ClueIds.Count(_storyFlags.Contains);

    /// <summary>누적 성향 — B7 자백의 태도를 가른다. 파생 계산(저장 안 함): 냉혹 ≤−3 / 중립 / 인간 ≥+3.</summary>
    private static readonly string[] EthosCold =
        { "fixed_once", "stayed_silent", "cato_sided", "punished", "infirmary_closed", "exec_accepted",
          "buried_quiet", "sent_hurt", "overworked", "sold_star" };
    private static readonly string[] EthosHuman =
        { "told_truth", "rookie_sided", "asked_why", "infirmary_open", "exec_refused",
          "carved_name", "rested_hurt", "rested_tired", "kept_star",
          "refused_fix" };   // 값을 치르고 거절하는 것이 1막에서 가장 인간적인 행위다(조작 수락 −1과 대칭)
    private int EthosScore => EthosHuman.Count(_storyFlags.Contains) - EthosCold.Count(_storyFlags.Contains);
    /// <summary>cold / mid / warm — 사실은 같고 카토가 왜 말하는가가 다르다.</summary>
    private string EthosBand => EthosScore <= -3 ? "cold" : EthosScore >= 3 ? "warm" : "mid";

    private bool SpawnStory(string templateId, string beatId, string? ctx = null)
    {
        _storyBeats.Add(beatId);
        _storyCtx = ctx;
        _pendingEventId = templateId;
        _pendingEventFighter = null;
        return true;
    }

    /// <summary>내 선수들이 치른 통산 경기 수 — 1막 페이싱 게이트(무레나는 시스템을 익힌 뒤에 온다).</summary>
    private int MyMatchesPlayed => _cast.Where(g => g.IsPlayer).Sum(g => g.CW + g.CL + g.CD);

    /// <summary>이번 시즌 진행률 0~1 — B7(카토의 자백)이 시즌 1의 2/3 지점에 오도록.</summary>
    private float SeasonProgress => _schedule.Count > 0 ? (float)_cursor / _schedule.Count : 0f;

    private bool HasMyMatchAheadNow() => SeasonActive &&
        Enumerable.Range(_cursor, Math.Max(0, _schedule.Count - _cursor))
            .Any(i => ById(_schedule[i].A).IsPlayer || ById(_schedule[i].B).IsPlayer);

    /// <summary>지목 격파(비트②) 표적 — 잔혹 가문의 간판 우선, 없으면 최고 명성 AI.</summary>
    private Gladiator? ChallengeTarget()
    {
        var blood = ActiveRivalLudi.Where(r => r.Persona == "blood").Select(r => r.Id).ToHashSet();
        return _cast.Where(g => !g.IsPlayer)
                    .OrderByDescending(g => blood.Contains(g.LudusId) ? 1 : 0)
                    .ThenByDescending(g => g.Fame).FirstOrDefault();
    }

    /// <summary>종막 판정(시즌말) — 내 모리튜리 1부 진입(승격) 또는 시즌 3 소프트 종료 → 라니스타가 되는 의식.</summary>
    private void CheckStoryFinale()
    {
        if (_playerless || _storyStage == "chronicle") return;
        bool inTop = _cast.Any(g => g.IsPlayer && g.Division == 1);
        if (!_promotedFlag && !inTop && _seasonsPlayed < 3) return;   // 각본이 샌드박스를 인질로 잡지 않는다(최대 3시즌)
        _storyStage = "chronicle";
        _storyBeats.Add("finale");
        _pendingEventId = "story_finale"; _pendingEventFighter = null; _storyCtx = null;   // 종막은 무엇보다 우선
        _story.Add((_rounds + 1, "story", "{ludus} 종막 — 카토: \"내가 가르칠 수 있는 건 여기까지입니다.\""));
    }

    // ── 스토리 이벤트 템플릿 (화자: 카토/무레나 — 기존 이벤트 카드 파이프 재사용) ──

    private string CtxLudusName => _storyCtx != null ? LudusNameOf(_storyCtx) : "경쟁 검투소";
    private Gladiator? MyFirst => _cast.FirstOrDefault(g => g.IsPlayer);
    /// <summary>남은 일정에 경기가 있는 내 선수(승부조작 예약 대상) — 없으면 첫 선수.</summary>
    private Gladiator? MyNextFighter()
    {
        if (SeasonActive)
            for (int i = _cursor; i < _schedule.Count; i++)
            {
                var a = ById(_schedule[i].A); var b = ById(_schedule[i].B);
                if (a.IsPlayer) return a;
                if (b.IsPlayer) return b;
            }
        return MyFirst;
    }

    private List<EvtTemplate> StoryTemplates() => new()
    {
        // ── 서막 S0 「유산」 — 장례 ──
        new EvtTemplate { Id = "story_s0", Icon = "{coffin}", Title = "유산", NeedsFighter = false,
            Body = _ => "비 오는 카푸아의 언덕. 선대 라니스타 가이우스의 장례에 조문객은 늙은 교관 하나뿐이다.\n" +
                "{speech} 카토: \"가이우스는 이길 수 없는 경기를 이겼습니다. 그리고 그날 밤 죽었지요. …남은 건 이 무너진 루두스와 빚, 그리고 접니다.\"\n" +
                "{speech} 카토: \"유서에 이렇게 적혀 있더군요 — '모래는 정직하다. 그 위의 인간들이 문제일 뿐.'\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("아버지의 유서를 품에 넣는다", _ => { AddGaiusWill(); return "유서를 품었다 — 보관함에 보관 (그는 무엇을 거절했던 걸까)"; }),
                ("무덤에 흙을 얹고 돌아선다", _ => { AddGaiusWill(); return "카토: \"…갑시다. 산 사람은 모래를 갈아야지요.\""; }) } },

        // ── 서막 S1 「빈 막사」 — 조각 1(도끼)·2(장부) ──
        new EvtTemplate { Id = "story_s1", Icon = "{ludus}", Title = "빈 막사", NeedsFighter = false,
            Body = _ => "장례를 마치고 돌아온 루두스. 카토가 막사 문을 연다. 침상 여덟 중 여섯이 비어 있다. " +
                "남은 둘에도 사람은 없다 — 담요만 개켜져 있다. 개켠 방식이 똑같다. 같은 사람이 개켰다는 뜻이다.\n" +
                "{speech} 카토: \"작년 겨울에 둘, 봄에 셋. 나머지 하나는 팔았습니다. 값은 빚으로 갔고요.\"\n" +
                "{speech} 카토: \"담요는 제가 갭니다. 아무도 없는데도 갭니다.\"\n" +
                "{speech} 카토: \"가이우스는 여덟을 다 채우고도 빚을 졌습니다. 당신은 둘로 시작하시는군요. …어느 쪽이 나은지는 저도 모르겠습니다.\"\n" +
                "연습장 쪽 벽에 도끼가 한 자루 걸려 있다. 자루가 손때로 검다. 날은 녹슬지 않았다 — 누군가 계속 닦고 있다는 뜻이다.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("저 도끼는 누구 것인지 묻는다", _ => {
                    Flag("axe_asked"); Flag("clue_axe");
                    AddKeepsake("단서", "벽에 걸린 도끼", "자루가 손때로 검다. 날은 녹슬지 않았다.\n\n" +
                        "카토: \"…저건 안 씁니다.\"\n\n" +
                        "그는 그 말만 하고 궤 쪽으로 걸어갔다. 걸어가면서 손등으로 도끼날을 한 번 스쳤다. 습관이었다.", "연습장 벽");
                    return "카토: \"…저건 안 씁니다.\" — 그 이상은 말하지 않았다"; }),
                ("말없이 막사를 둘러본다", _ => "카토는 담요를 한 번 더 매만졌다. 그게 그가 하는 인사였다") } },

        // ── 서막 S2 「궤」 — 조각 2(장부). S1과 별도 씬: 도끼와 장부는 택일이 아니다 ──
        new EvtTemplate { Id = "story_s2", Icon = "{scroll}", Title = "궤", NeedsFighter = false,
            Body = _ => "카토가 벽 쪽 궤를 턱으로 가리킨다. 자물쇠는 이미 부서져 있다.\n" +
                "{speech} 카토: \"장부는 저 안에 있습니다. 열어보시겠습니까. …열든 안 열든 숫자는 그대로입니다만.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("장부를 연다", _ => {
                    Flag("ledger_read"); Flag("clue_ledger");
                    AddKeepsake("메모", "검은 인장 장부", "숫자는 컸다. 그런데 눈에 걸린 것은 다른 것이었다.\n\n" +
                        "15년 전까지 매달 반복되던 지출 한 줄 — 「의원 비용 — O.」\n" +
                        "다른 이름은 전부 온전히 적혀 있는데, 이것만 이니셜이다. 그리고 어느 달부터 그냥 끊겨 있다.\n\n" +
                        "카토: \"…오래된 겁니다. 신경 쓰지 마십시오.\"\n" +
                        "그는 장부를 덮으려다 말았다. 덮으면 더 이상해진다는 걸 아는 사람의 손이었다.", "루두스 장부");
                    return "장부를 읽었다 — 카토: \"가이우스도 그 자리에 그렇게 앉아 있었습니다. 숫자를 다 읽고도 도망치지 않았지요.\""; }),
                ("덮는다", _ => { Flag("ledger_unread"); return "카토: \"현명하십니다. …아직은요.\""; }) } },

        // ── 서막 S3 「첫 훈련」 — 목검(의지) ──
        new EvtTemplate { Id = "story_s3", Icon = "{sword}", Title = "첫 훈련", NeedsFighter = false,
            Body = _ => "연습장. 신참이 목검을 내던진다. 카토가 주워 다시 건넨다. 세 번째다. " +
                "목검 손잡이에 이미 금이 가 있다 — 던져서 그런 게 아니라, 쥐는 힘이 잘못돼서 그렇다.\n" +
                "{speech} 카토: \"라니스타. 이 아이가 그러더군요. 자기는 검을 배우러 온 게 아니라 죽으러 온 거라고.\"\n" +
                "{speech} 카토: \"틀린 말은 아닙니다. 순서가 틀렸을 뿐이지요. 배우고 나서 죽는 겁니다.\"\n" +
                "그가 당신을 본다. 결정을 미루는 눈이다. 그는 이런 눈을 잘 하지 않는다.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("카토의 방식대로 하라", _ => {
                    Flag("cato_sided");
                    var f = MyFirst; if (f != null) f.Fatigue = Math.Min(100, f.Fatigue + 15);
                    return "그날 밤 연습장의 불은 늦게까지 꺼지지 않았다 — 카토: \"셋째 날부터는 안 던지더군요. 넷째 날부터는 말도 안 하고요.\""; }),
                ("아이의 말을 들어보자", _ => {
                    Flag("rookie_sided");
                    return "카토: \"가이우스도 그랬습니다. 사람 말을 다 들었지요. 그래서 저는 아직도 그 사람이 밉습니다.\"" +
                        " — 다음 날 아침 신참은 목검을 던지지 않았고, 금 간 손잡이를 천으로 감아 두었다"; }),
                ("둘 다 모래 위에 세워둔다", _ => {
                    Flag("both_stood");
                    var f = MyFirst; if (f != null) f.Fatigue = Math.Min(100, f.Fatigue + 8);
                    return "카토가 웃었다. 소리는 나지 않았다 — \"좋습니다. 그게 라니스타지요. 저는 편을 들 줄만 알아서요.\""; }) } },

        // ── 서막 S4 「의무실」 — 테아 등장 · 조각 5 ──
        new EvtTemplate { Id = "story_s4", Icon = "{medic}", Title = "의무실", NeedsFighter = false,
            Body = _ => "막사 끝, 등불 하나. 약재와 식초 냄새. 마른 여인이 붕대를 삶고 있다. " +
                "삶은 붕대를 널어 말리는 줄이 방을 가로지르는데, 줄이 낡아서 가운데가 처져 있다.\n" +
                "{speech} 테아: \"…아, 거기 서 계시면 안 됩니다. 젖어요.\"\n" +
                "당신이 한 발 옮긴다. 그녀는 고개도 들지 않았다.\n" +
                "{speech} 테아: \"새 주인이시군요. 가이우스는 매달 이 방 값을 치렀습니다. 밀린 적은 한 번도 없었고요.\"\n" +
                "{speech} 테아: \"손 씻으셨습니까. 아니면 아무것도 만지지 마시고요.\"\n" +
                "그녀가 붕대를 건져 짠다. 물이 바닥으로 떨어진다. 한참 그러고 있다.\n" +
                "{speech} 테아: \"저는 말리지 않습니다, 라니스타. 세어드릴 뿐이지요 — 저 아이가 몇 번 더 뛸 수 있는지.\"\n" +
                "그녀가 선반을 가리킨다. 항아리들이 비어 있다.\n" +
                "{speech} 테아: \"지금은 셀 것도 없습니다만.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("의무실을 유지한다 (골드 −60)", _ => {
                    // 시작 금고(50)로는 못 낸다 — 돌보겠다는 선택 자체는 냉혹으로 세지 않는다(의도는 처벌하지 않는다).
                    if (_gold < 60f) { Flag("infirmary_tried");
                        return "금고가 모자란다 — 테아: \"…돈이 없으신 거지, 마음이 없으신 건 아니군요.\" 그녀는 항아리를 엎지 않았다"; }
                    _gold -= 60f; Flag("infirmary_open");
                    return "골드 −60, 항아리가 다시 찼다 — 테아: \"…아버지 같으시군요. 칭찬은 아닙니다. 그분은 이 방에 돈을 쓰고 다른 데서 빌렸으니까요.\""; }),
                ("당분간 닫는다", _ => {
                    Flag("infirmary_closed");
                    return "그녀는 화내지 않았다. 항아리를 하나씩 엎어놓기 시작했다. 그게 더 나빴다"; }),
                ("당신은 얼마나 여기 있었는지 묻는다", _ => {
                    Flag("clue_thea");
                    AddKeepsake("단서", "테아의 햇수", "테아: \"열아홉 해입니다.\"\n\n" +
                        "테아: \"…그 전 해에 여기서 사람이 하나 죽었지요. 그래서 자리가 났고요.\"\n\n" +
                        "그녀는 그 이상 말하지 않았다. 붕대를 널던 손도 멈추지 않았다.", "약제사 테아");
                    return "테아: \"열아홉 해입니다. …그 전 해에 여기서 사람이 하나 죽었지요. 그래서 자리가 났고요.\""; }) } },

        // ── 서막 S5 「개막 전야」 — 조각 7(함정: 이긴 사람은 가이우스가 아니다) ──
        new EvtTemplate { Id = "story_s5", Icon = "{wine}", Title = "개막 전야", NeedsFighter = false,
            Body = _ => "개막 하루 전. 막사는 조용하다. 카토가 탁자에 앉아 있다.\n" +
                "{speech} 카토: \"밥은 드셨습니까.\"\n" +
                "당신이 대답한다. 그는 고개를 끄덕이고 아무 말도 하지 않는다.\n" +
                "{speech} 카토: \"저 문짝이 또 삐걱거립니다. 3년째 그럽니다. 고치려다 말았고요.\"\n" +
                "그가 포도주를 두 잔 따른다. 그리고 한 잔을 바닥에 붓는다.\n" +
                "{speech} 카토: \"습관입니다. 20년을 그렇게 마셨거든요, 저 사람하고.\"\n" +
                "그가 남은 잔을 당신에게 건넨다.\n" +
                "{speech} 카토: \"내일이면 당신은 라니스타입니다. 오늘까지는 그냥 아들이고요.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("왜 죽었는지 묻는다", _ => {
                    Flag("asked_about_gaius"); Flag("clue_recall");
                    AddKeepsake("메모", "카토의 회상 ①", "카토: \"…마지막 시즌이었습니다. 져야 할 경기가 하나 있었지요. 온 도시가 알고 있었습니다.\"\n\n" +
                        "카토: \"그런데 이겼습니다.\"\n\n" +
                        "그의 말이 조금 이상하게 들렸다. 누가 이겼다는 것인지 명확하지 않았다. 당신은 아버지 이야기라고 이해했다.\n\n" +
                        "카토: \"이겼고, 아무도 축하하지 않았습니다. …저도요.\"\n\n" +
                        "그가 잔을 내려놓았다. 더 묻지 말라는 뜻이다.", "교관 카토");
                    return "카토: \"그런데 이겼습니다. …이겼고, 아무도 축하하지 않았습니다. 저도요.\""; }),
                ("묻지 않는다", _ => "카토: \"…예. 언젠가 물으실 겁니다. 그때 대답하지요.\" — 그는 빈 잔을 오래 들여다보았다"),
                ("말없이 잔을 받아 마신다", _ => {
                    Flag("shared_cup");
                    return "두 사람은 빈 연습장을 오래 바라보았다. 벽의 도끼가 등불에 한 번 번뜩였다" +
                        " — 카토: \"…내일 뵙겠습니다, 라니스타.\" 그가 당신을 그렇게 부른 것은 처음이다"; }) } },

        // ── 1막 A0 「개막」 — 위쪽 세 줄(무레나의 예고) ──
        new EvtTemplate { Id = "story_a0", Icon = "{arena}", Title = "개막", NeedsFighter = false,
            Body = _ => "개막일. 관중석은 반쯤 찼다. 당신 이름을 부르는 사람은 없다.\n" +
                "{speech} 카토: \"저기 위쪽 세 줄, 저 사람들이 판돈을 정리하는 자들입니다. 우리 경기를 보러 온 게 아니라, 우리가 얼마짜리인지 보러 왔지요.\"\n" +
                "{speech} 카토: \"오늘은 그냥 이기십시오. 나머지는 다음에 생각해도 됩니다.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("위쪽 세 줄을 올려다본다", _ => "아무도 당신과 눈을 마주치지 않았다. 세는 중이었기 때문이다"),
                ("모래만 본다", _ => "카토: \"…그게 낫습니다. 모래는 적어도 정직하니까요.\"") } },

        // ── 1막 A3 「빚에 얼굴이 생긴 날」 — 무레나 첫 등장(조작 제안 없음: 첫 번째는 그냥 받아 간다) ──
        new EvtTemplate { Id = "story_a3", Icon = "{candle}", Title = "빚에 얼굴이 생긴 날", NeedsFighter = false,
            Body = _ => "시즌 첫 상환일. 문 두드리는 소리는 정중했다. 세 번, 고르게. 급한 사람의 소리가 아니었다.\n" +
                "값비싼 토가를 입은 사내가 들어와 앉는다. 앉기 전에 의자를 손등으로 한 번 훑었다. 먼지를 확인한 게 아니라, 이 방이 어떤 방인지 재는 손이었다.\n" +
                "{speech} 무레나: \"중개인 무레나라고 합니다. 검은 인장의 일을 봅니다.\"\n" +
                "{speech} 무레나: \"가이우스의 후계자시군요. 빚은 피를 가리지 않습니다. …그렇다고 제가 피를 원하는 건 아니고요. 저는 숫자만 원합니다.\"\n" +
                (_storyFlags.Contains("ledger_unread")
                    ? "{speech} 무레나: \"이자가 붙었습니다. …모르셨습니까? 궤 안에 다 적혀 있었을 텐데요.\"\n옆에서 카토가 아무 말도 하지 않는다. 그게 대답이다.\n"
                    : "{speech} 무레나: \"장부를 보셨더군요. 그럼 설명은 생략하겠습니다. 편합니다, 이런 분이.\"\n") +
                "{speech} 무레나: \"오늘은 두 가지 중 하나입니다. 돈, 아니면 시간.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("갚는다 (빚의 40%)", _ => {
                    float pay = MathF.Round(_debt * 0.4f);
                    if (pay <= 0f) return "갚을 빚이 장부에 없다 — 무레나는 고개를 끄덕이고 일어섰다. \"그럼 다음에 뵙지요.\"";
                    if (_gold < pay) { DebtTxn("유예의 값 — 금고가 모자랐다", MathF.Round(_debt * 0.25f));
                        return $"금고가 모자란다 (필요 {pay:F0}) — 무레나: \"…시간으로 하시지요. 비싸지만요.\" (빚 +25%)"; }
                    _gold -= pay; DebtTxn("첫 상환 — 무레나의 방문", -pay); AddRep(5f);
                    return $"골드 −{pay:F0}, 명성 +5 — 무레나: \"…아, 정말로 갚으시는군요. 이런 날은 적어둡니다. 드물어서요.\""; }),
                ("미룬다 (빚 +25%)", _ => {
                    DebtTxn("유예의 값 — 시간은 제일 비싼 물건", MathF.Round(_debt * 0.25f));
                    return "무레나: \"물론입니다. 시간은 제가 파는 물건 중 제일 비쌉니다. …비싸다는 건 값이 있다는 뜻입니다. 다음에 말씀드리지요.\""; }),
                ("아버지를 아느냐 묻는다 (빚 +25%)", _ => {
                    Flag("murena_first_job");
                    AddKeepsake("단서", "무레나의 첫 일", "무레나: \"압니다. 20년 됐지요.\"\n\n" +
                        "무레나: \"제 첫 일이었습니다. 그때 저는 당신만큼 젊었고, 지금보다 훨씬 나은 사람이었습니다.\"\n\n" +
                        "그가 잠깐 말을 멈췄다. 그리고 웃으며 증서를 정리했다.\n" +
                        "무레나: \"…쓸데없는 얘길 했군요.\"", "중개인 무레나");
                    DebtTxn("유예의 값 — 대답을 들은 날", MathF.Round(_debt * 0.25f));
                    return "무레나: \"제 첫 일이었습니다. …쓸데없는 얘길 했군요. 오늘은 시간으로 적어두지요.\" (빚 +25%)"; }) } },

        // ── 1막 A4 「관중의 맛」 — 루킬리우스 등장 ──
        new EvtTemplate { Id = "story_a4", Icon = "{masks}", Title = "관중의 맛", NeedsFighter = false,
            Body = _ => "흥행이 붙기 시작한 날. 관중석 아래에서 향수 냄새가 나는 남자가 손을 흔든다.\n" +
                "{speech} 루킬리우스: \"오, 그 유명한 죽은 사람의 아들! 아니 실례, 라니스타. 조영관 루킬리우스입니다. 이 도시의 재미를 발주하는 사람이지요.\"\n" +
                "{speech} 루킬리우스: \"아, 오다가 저 계단에서 넘어질 뻔했어요. 저거 누가 좀 고쳐야 합니다. 제가 말할 데가 아닌가? 제가 말할 데네요. 하하.\"\n" +
                "{speech} 루킬리우스: \"어디까지 했죠. 아, 예.\"\n" +
                "{speech} 루킬리우스: \"군중은 정의를 원하지 않습니다. 이야기를 원하지요. …당신 아이에게 이야기가 있습니까?\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("이야기를 만들어 주겠다 (인기 +8 · 후원 +5)", _ => {
                    Flag("hype_courted"); var f = MyFirst; if (f != null) f.Popularity += 8f; Patron(5f);
                    return "루킬리우스: \"좋아요! 저는 야심 있는 분이 좋습니다. 오래 못 살거든요. …농담입니다. 절반은요.\""; }),
                ("우리는 싸울 뿐이다 (명성 +5 · 후원 −5)", _ => {
                    Flag("hype_refused"); AddRep(5f); Patron(-5f);
                    return "루킬리우스: \"…아, 아버님이랑 똑같이 말씀하시는군요. 그분도 재미없었어요. 재미없는 분들은 이 도시에서 오래 못 가시더라고요.\""; }) } },

        // ── 1막 A5 「두 번째 방문」 — 조작 최초 제안(파는 건 두 번째부터) ──
        new EvtTemplate { Id = "story_a5", Icon = "{candle}", Title = "두 번째 방문", NeedsFighter = false,
            Body = _ => "두 번째다. 이번엔 문을 두드리지 않았다. 이미 열려 있었기 때문이다 — 누가 열어놨는지는 아무도 말하지 않았다.\n" +
                "무레나가 증서 뭉치를 탁자에 올려놓는다. 인장은 검다.\n" +
                "{speech} 무레나: \"지난번엔 숫자만 말씀드렸지요. 오늘은 방법을 말씀드리려고 왔습니다.\"\n" +
                "{speech} 무레나: \"당신 모리튜리가 적당한 날에 적당히 져 주기만 하면 됩니다. 그날 하루입니다.\"\n" +
                "{speech} 무레나: \"우린 아무도 죽이지 않아요, 라니스타. 당신들이 돈 때문에 죽이는 거죠. 우린 그저 결과를 정리할 뿐입니다.\"\n" +
                "카토는 문가에 서 있다. 들어오지 않는다. 손에 닦다 만 갑옷이 들려 있다. 그는 그걸 계속 쥐고만 있다.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("고개를 끄덕인다 (다음 경기를 던지면 골드 +160)", _ => {
                    var f = MyNextFighter(); if (f == null) return "던질 모리튜리가 없다 — 무레나가 조용히 증서를 거뒀다";
                    _fixFighterId = f.Id; _fixReward = 160f; _fixChoice = "accept"; Flag("fixed_once");
                    return $"{{candle}} 검은 거래 — {f.Name}이(가) 다음 경기를 던져야 한다. 무레나: \"현명하십니다. 아버님보다 훨씬.\" 카토의 갑옷은 끝내 다 닦이지 않았다"; }),
                ("증서를 밀어낸다 (명성 +8)", _ => {
                    AddRep(8f); _fixChoice = "refuse"; Flag("refused_fix");
                    AddClue("무레나 — \"당신 아버지는 8년을 거절했습니다. 8년이요. 그리고 딱 한 번, 거절할 수 없는 게 왔지요. 그는 그것도 이겼습니다. …그래서 뭘 얻었습니까?\"");
                    return "명성 +8 — 그가 나간 뒤 카토가 갑옷을 마저 닦았다. 아주 오래 닦았다"; }),
                ("얼마냐고 먼저 묻는다", _ => {
                    Flag("asked_price");
                    return "무레나: \"…아, 그걸 먼저 물으시는 분은 오랜만입니다. 값을 물으면 사시는 겁니다, 보통은. 오늘 아니면 내년에라도요.\" 그는 액수를 말하고, 답은 다음에 받으러 오겠다며 일어섰다"; }) } },

        // ── 1막 A5′ 「답을 받으러 온 날」 — 값을 물은 자에게만 ──
        new EvtTemplate { Id = "story_a5b", Icon = "{candle}", Title = "답을 받으러 온 날", NeedsFighter = false,
            Body = _ => "그가 다시 왔다. 이번에는 앉지도 않았다.\n" +
                "{speech} 무레나: \"생각해 보셨습니까. 골드 200 — 지난번보다 올랐습니다. 값을 물으신 분이라 특별히요.\"\n" +
                "{speech} 무레나: \"궁금해하는 것도 값이 붙습니다, 라니스타. 이 도시에서는요.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("고개를 끄덕인다 (다음 경기를 던지면 골드 +200)", _ => {
                    var f = MyNextFighter(); if (f == null) return "던질 모리튜리가 없다 — 무레나가 조용히 증서를 거뒀다";
                    _fixFighterId = f.Id; _fixReward = 200f; _fixChoice = "accept"; Flag("fixed_once");
                    return $"{{candle}} 검은 거래 — {f.Name}이(가) 다음 경기를 던져야 한다. 무레나: \"거래는 언제나 두 번째가 쉽지요.\""; }),
                ("거절한다 (명성 +8)", _ => {
                    AddRep(8f); _fixChoice = "refuse"; Flag("refused_fix");
                    return "명성 +8 — 무레나: \"…예. 값만 알고 안 사는 분도 있지요. 제일 위험한 손님입니다.\""; }) } },

        // ── 1막 A6 「검을 닦지 않은 밤」 — 조작의 꼬리(fixed_once일 때만) ──
        new EvtTemplate { Id = "story_a6", Icon = "{speech}", Title = "검을 닦지 않은 밤", NeedsFighter = false,
            Body = _ => "조작 경기로부터 사흘. 카토가 밤에 찾아왔다. 그는 밤에 오지 않는 사람이다.\n" +
                $"{{speech}} 카토: \"{MyFirst?.Name ?? "그 아이"}이(가) 묻더군요. 왜 자기만 이기지 못하냐고.\"\n" +
                "{speech} 카토: \"자기 몸이 잘못된 건지, 제가 잘못 가르친 건지 알고 싶답니다.\"\n" +
                "{speech} 카토: \"…그 녀석, 오늘 밤엔 검을 닦지 않고 그냥 누웠습니다. 2년 동안 하루도 안 빼먹던 녀석인데요.\"\n" +
                "{speech} 카토: \"자기가 왜 검을 닦아야 하는지 모르는 눈이었습니다.\"\n" +
                "{speech} 카토: \"저는 대답하지 못했습니다. …두 번째입니다, 대답 못 한 게.\"\n" +
                "두 번째라는 말의 첫 번째가 언제인지는 말하지 않았다.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("진실을 말한다 (명성 +8 · 인기 −5)", _ => {
                    Flag("told_truth"); AddRep(8f); var f = MyFirst; if (f != null) f.Popularity = MathF.Max(0f, f.Popularity - 5f);
                    return "카토: \"…예. 제가 전하겠습니다.\" — 다음 날 훈련에 나오지 않았고, 그 다음 날은 나왔다. \"어젯밤엔 검을 닦았답니다. 아주 오래요. 날이 상할 만큼.\""; }),
                ("다시는 없다고 약속한다", _ => {
                    Flag("promised");
                    return "카토: \"약속을 전할까요, 아니면 지키실 겁니까. …둘 다 하시길 바랍니다. 저는 하나만 한 적이 있어서요.\""; }),
                ("침묵한다", _ => {
                    Flag("stayed_silent");
                    return "그는 더 묻지 않고 나갔다. 문은 조용히 닫혔다. 그날부터 카토의 경기평은 한 줄 더 짧아졌다"; }) } },

        // ── 1막 A7 「이름이 불렸다」 — 값이 매겨지기 시작한다 ──
        new EvtTemplate { Id = "story_a7", Icon = "{horn}", Title = "이름이 불렸다", NeedsFighter = false,
            Body = _ => "시장에서 처음으로 누군가 당신 루두스의 이름을 말했다.\n" +
                "{speech} 카토: \"우리 이름이 들렸습니다. 좋은 쪽으로는 아니었지만, 들리긴 했습니다.\"\n" +
                "{speech} 카토: \"들리기 시작하면 값이 매겨집니다. 값이 매겨지면 사람들이 찾아오고요.\"\n" +
                "{speech} 카토: \"…아이들도 압니다. 어제부터 갑옷을 다시 닦기 시작했거든요. 누가 시킨 것도 아닌데요.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("갑옷걸이를 보러 간다", _ => "전부 걸려 있었다. 전부 닦여 있었다. 그런 날이 있다"),
                ("하던 일을 계속한다", _ => "카토: \"예. 그것도 대답입니다.\"") } },

        // ── 1막 비트① 「세 가문」 — 개성별 환영 ──
        new EvtTemplate { Id = "story_house_gold", Icon = "{coin}", Title = "재력가의 환영", NeedsFighter = false, Kind = "letter",
            Body = _ => $"{CtxLudusName}의 사절이 금박 두루마리를 펼친다.\n" +
                $"{{speech}} 사절: \"주인께서 새 얼굴에게 인사를 전하랍니다 — '당신 별 하나, 값을 매겨 왔습니다. 언제든 파실 마음이 생기면.' 프리시즌의 이적 시장에서 뵙지요.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("성의만 받는다 (골드 +60)", _ => { _gold += 60f; return "골드 +60 — \"첫 거래 치곤 나쁘지 않군요.\" (프리시즌 이적 시장이 열려 있다)"; }),
                ("금화를 돌려보낸다 (명성 +8)", _ => { AddRep(8f); return "명성 +8 — \"돈으로 안 되는 라니스타라… 비싸지겠군.\""; }) } },

        new EvtTemplate { Id = "story_house_youth", Icon = "{sprout}", Title = "육성가의 환영", NeedsFighter = false,
            Body = _ => $"{CtxLudusName}의 노(老)스카우터가 훈련장을 말없이 둘러보다 입을 연다.\n" +
                $"{{speech}} 스카우터: \"원석은 눈이 아니라 인내로 캡니다. 당신이 놓친 원석, 우리가 주워갈 겁니다 — 서로 좋은 경쟁이 되길.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("경쟁을 받아들인다 (인기 +4)", g => { var f = MyFirst; if (f != null) f.Popularity += 4f; return "군중이 두 양성소의 경쟁을 반긴다 — 인기 +4"; }),
                ("훈련장에서 정중히 배웅한다 (명성 +5)", _ => { AddRep(5f); return "명성 +5 — \"예의는 아는 친구로군.\""; }) } },

        new EvtTemplate { Id = "story_house_blood", Icon = "{blood}", Title = "잔혹가의 도발", NeedsFighter = false, Kind = "letter",
            Body = _ => $"{CtxLudusName}의 인장이 찍힌 서신 — 피 냄새가 나는 환영 인사다.\n" +
                $"{{speech}} 서신: \"우리는 당신 이름을 모릅니다. 당신 아버지 이름은 압니다. 그가 어떻게 죽었는지도. 카푸아에서 그걸 모르는 라니스타는 당신뿐일 겁니다.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("서신을 태운다 (명성 +2)", _ => { AddRep(2f); return "명성 +2 — 카토가 재를 치웠다. 아무 말도 하지 않았다"; }),
                ("보관한다 (인기 +6, 원한)", g => {
                    Flag("clue_letter");
                    var f = MyFirst; if (f == null) return "답할 모리튜리가 없다";
                    f.Popularity += 6f;
                    var t = PickGrudgeTarget(f, _storyCtx);
                    if (t != null) { _ledger.DeepenGrudge(f.Id, t.Id, 22f); return $"{f.Name} 인기 +6 — 서신을 보관하고, {t.Name}({LudusNameOf(t.LudusId)})에게 원한을 새겼다"; }
                    return $"{f.Name} 인기 +6 — 서신은 보관함에 남았다"; }),
                ("카토에게 보여준다 (명성 +6)", _ => {
                    Flag("clue_letter"); AddRep(6f);
                    return "명성 +6 — 카토: \"…도시 사람들은 다 압니다, 라니스타. 정확히는 모르고, 대충 압니다. 대충 아는 게 제일 나쁩니다. 그러면 아무도 안 물어보거든요.\""; }) } },

        // ── 2막 B1 「첫 피」 — 테아가 값을 센다 ──
        new EvtTemplate { Id = "story_b_blood", Icon = "{medic}", Title = "첫 피", NeedsFighter = false,
            Body = _ => "의무실. 테아가 상처를 씻고 있다. 소리를 내지 않는다 — 소리를 내면 값이 떨어진다는 걸 아는 것이다.\n" +
                "벗어놓은 갑옷이 바닥에 있는데, 오늘은 아무도 그걸 세워두지 않았다.\n" +
                "{speech} 테아: \"붕대 좀 건네주십시오. 거기 왼쪽이요.\"\n" +
                "당신이 건넨다. 그녀는 고맙다는 말을 하지 않는다.\n" +
                (_storyFlags.Contains("infirmary_closed")
                    ? "{speech} 테아: \"항아리는 여전히 비어 있습니다. 말씀드렸었지요. 오늘 쓴 건 제 돈으로 샀습니다.\"\n"
                    : "") +
                "{speech} 테아: \"…세 경기입니다. 그 전에 내보내시면 그 다음은 장담 못 합니다.\"\n" +
                "{speech} 테아: \"오늘은 늦었습니다. 가서 주무십시오.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("세 경기 쉬게 한다 (명성 +3)", _ => {
                    Flag("rested_hurt"); AddRep(3f);
                    return "명성 +3 — 테아: \"…예. 적어두겠습니다.\""; }),
                ("다음 경기에 내보낸다", _ => {
                    Flag("sent_hurt");
                    var f = _cast.FirstOrDefault(g => g.IsPlayer && g.InjuryMatches > 0);
                    if (f != null) f.Fatigue = Math.Min(100, f.Fatigue + 10);
                    return "그녀는 반박하지 않았다. 다만 장부에 무언가를 적었다 — \"제가 뭘 적는지는 안 물으시는군요. 다들 안 물으십니다.\""; }) } },

        // ── 2막 B3 「피로도가 낳은 괴물」 — 테아가 처음으로 화를 낸다 (B7 복선) ──
        new EvtTemplate { Id = "story_b_monster", Icon = "{medic}", Title = "피로도가 낳은 괴물", NeedsFighter = false,
            Body = _ => "이겼다. 관중이 일어섰다. 그리고 막사로 돌아온 아이가 문턱을 넘다가 그대로 쓰러졌다.\n" +
                "숨을 쉬는데 소리가 났다. 사람 몸에서 날 소리가 아니었다.\n" +
                "아무도 놀라지 않았다. 그게 제일 나빴다 — 다들 예상하고 있었다는 뜻이니까.\n" +
                "테아가 라니스타의 방으로 왔다. 노크는 없었다. 그녀가 청구서를 탁자에 던진다. 던진 것은 처음이다.\n" +
                "{speech} 테아: \"이겼으니 가방이 두둑하시겠군요, 라니스타. 기뻐하십시오.\"\n" +
                "{speech} 테아: \"다만 다음 주엔 그 아이의 숨통 대신 당신의 골드를 모래 위에 세우셔야 할 겁니다.\"\n" +
                "{speech} 테아: \"…찢어진 살은 꿰맵니다. 터지기 직전인 심장은 못 바꾸고요.\"\n" +
                "그녀가 문 쪽으로 가다가 멈춰 선다.\n" +
                "{speech} 테아: \"20년 전에 이 방에서 똑같은 걸 봤습니다. 그때 그 사람 숨소리가 이랬지요.\"\n" +
                "{speech} 테아: \"그때는 아무도 저한테 안 물었습니다. 저는 그냥 세기만 했고요.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("당분간 쉬게 한다", _ => {
                    Flag("rested_tired");
                    var f = _cast.Where(g => g.IsPlayer).OrderByDescending(g => g.Fatigue).FirstOrDefault();
                    if (f != null) f.Fatigue = Math.Max(0, f.Fatigue - 40);
                    return "피로 −40 — 테아: \"…예. 그럼 세겠습니다. 세는 게 제 일이니까요.\" 그날 그녀는 항아리를 하나 새로 채웠다"; }),
                ("이번 시즌만 버틴다", _ => {
                    Flag("overworked");
                    return "테아: \"알겠습니다. …한 가지만요. 그 사람도 '한 시즌만'이라고 했습니다.\" 그 말이 왜 그렇게 무거웠는지는 나중에 알게 된다"; }),
                ("20년 전 무슨 일이었는지 묻는다", _ => {
                    Flag("clue_thea");
                    AddKeepsake("단서", "테아가 본 것", "테아: \"…제 입으로 할 얘기가 아닙니다.\"\n\n" +
                        "테아: \"저 사람한테 물으십시오. 그게 순서고요.\"\n\n" +
                        "그녀는 '저 사람'이 누구인지 말하지 않았다. 말할 필요가 없었다.", "약제사 테아");
                    return "테아: \"…제 입으로 할 얘기가 아닙니다. 저 사람한테 물으십시오. 그게 순서고요.\""; }) } },

        // ── 2막 B5 「담장 아래」 — S3·A6의 회수 ──
        new EvtTemplate { Id = "story_b_wall", Icon = "{ludus}", Title = "담장 아래", NeedsFighter = false,
            Body = _ => "새벽. 담장 아래 흙이 파여 있다. 반쯤. 카토가 그 앞에 서 있다. 한참 전부터 거기 있었던 것 같았다.\n" +
                "파낸 흙이 옆에 가지런히 쌓여 있다. 도망치려던 사람의 손이 아니다.\n" +
                $"{{speech}} 카토: \"{MyFirst?.Name ?? "그 아이"}입니다. 반쯤 파고 그만뒀더군요.\"\n" +
                "{speech} 카토: \"도망칠 수 있었는데 안 갔습니다. 그게 더 문제입니다. …이건 도망이 아니라 물어보는 겁니다.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("본보기로 벌한다 (명성 +5)", _ => {
                    Flag("punished"); AddRep(5f);
                    var f = MyFirst; if (f != null) f.Fatigue = Math.Min(100, f.Fatigue + 12);
                    return "명성 +5 — 그날 훈련은 완벽했다. 완벽해서 무서웠다. 그리고 그날 밤, 막사에서 검 닦는 소리가 하나도 나지 않았다"; }),
                ("못 본 척한다", _ => {
                    Flag("looked_away");
                    return "카토: \"제가 흙을 덮겠습니다. …한 번은요.\""; }),
                ("왜 그만뒀는지 묻는다 (인기 +10)", _ => {
                    Flag("asked_why"); var f = MyFirst; if (f != null) f.Popularity += 10f;
                    return "아침에 그 아이는 흙을 다시 덮고 있었다. 혼자서, 삽도 없이 손으로 — 카토: \"저는 20년 동안 저걸 한 번도 안 해봤습니다. …물어봤으면 달랐을 일이 하나 있었는데요.\""; }) } },

        // ── 2막 B6 「루킬리우스의 발주」 — 처형전 ──
        new EvtTemplate { Id = "story_b_exec", Icon = "{masks}", Title = "루킬리우스의 발주", NeedsFighter = false,
            Body = _ => "조영관이 직접 찾아왔다. 이번엔 향수를 더 뿌렸다.\n" +
                "{speech} 루킬리우스: \"이 도시 여름은 정말 못 살겠습니다. 아, 제 얘깁니다.\"\n" +
                "{speech} 루킬리우스: \"도시가 흉흉합니다. 군중이 배가 고파요. 저는 배고픈 군중이 제일 무섭습니다 — 표를 안 사거든요.\"\n" +
                "{speech} 루킬리우스: \"처형전 하나 넣읍시다. 당신 아이가 이기면 이 도시가 그 이름을 외울 겁니다. …지면, 뭐, 그것도 외우긴 하겠네요.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("받는다 (인기 +20 · 후원 +10)", _ => {
                    Flag("exec_accepted"); var f = MyFirst; if (f != null) f.Popularity += 20f; Patron(10f);
                    return "루킬리우스: \"좋아요! 이래야 장사죠.\" — 그가 나간 뒤 카토가 오래 서 있었다. \"저 사람은 우리 아이 이름을 아직도 안 물어봤습니다. 20년째 그럽니다. 저 사람도, 그 위도요.\""; }),
                ("거절한다 (후원 −15 · 인기 −10)", _ => {
                    Flag("exec_refused"); Patron(-15f);
                    var f = MyFirst; if (f != null) f.Popularity = MathF.Max(0f, f.Popularity - 10f);
                    return "루킬리우스: \"아쉽네요. 진심으로. 아버님도 딱 그 표정이셨습니다. 그때도 저는 아쉬웠고요.\" 그 말이 왜 위협처럼 들렸는지는 나중에 알게 된다"; }) } },

        // ── 2막 B7 「20년」 — 카토의 자백. 사실은 하나, 태도는 에토스가 정한다 ──
        new EvtTemplate { Id = "story_b_confess", Icon = "{candle}", Title = "20년", NeedsFighter = false,
            Body = _ => ConfessionBody(),
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("나가라", _ => {
                    Flag("cato_exiled"); AddRep(10f); ArchiveConfession();
                    return "그는 목검 하나만 들고 나갔다. 20년 동안 가르친 것들은 두고 갔다. 도끼도 그대로 두었다 — \"저 아이들은 잘못이 없습니다. 그것만 기억해 주십시오. …날은 계속 닦아 주십시오. 부탁입니다.\" (명성 +10 · 훈련 효율 저하 · 경기평의 화자가 바뀐다)"; }),
                ("당신은 여기 남는다", _ => {
                    Flag("cato_kept"); ArchiveConfession();
                    return "카토: \"…왜입니까. 용서하지 마십시오. 그건 제 몫이 아니라 죽은 사람 몫입니다.\" 당신은 대답하지 않았다 — \"…예. 그럼 계속 가르치겠습니다. 그게 제일 무거운 벌이니까요.\" 그날 이후 그의 경기평에서 '도끼' 이야기는 사라졌다"; }),
                ("지금은 대답하지 않겠다", _ => {
                    Flag("cato_unanswered"); ArchiveConfession();
                    return "카토: \"…예. 기다리겠습니다.\" 그는 다음 날도 연습장에 있었다. 그 다음 날도. 아무 일 없다는 듯이"; }) } },

        // ── 2막 B8 「무레나가 안다는 것」 — 자백 직후 ──
        new EvtTemplate { Id = "story_b_murena", Icon = "{candle}", Title = "무레나가 안다는 것", NeedsFighter = false,
            Body = _ => "다음 상환일. 무레나는 당신 얼굴을 보자마자 증서를 내려놓지 않았다.\n" +
                "{speech} 무레나: \"…비가 오는군요. 우산을 안 가져왔습니다.\"\n" +
                "{speech} 무레나: \"교관이 말했군요. 표정을 보면 압니다.\"\n" +
                "{speech} 무레나: \"저는 그걸 20년 동안 안 썼습니다, 라니스타. 왜인지 아십니까?\"\n" +
                "{speech} 무레나: \"쓸 필요가 없었으니까요. 저 사람은 이미 자기가 자기를 쓰고 있었거든요.\"\n" +
                (_storyFlags.Contains("murena_first_job")
                    ? "{speech} 무레나: \"…제 첫 일이라고 했지요. 저는 은퇴 카드로 쓰라고 올렸습니다. 위에서는 대진표로 썼고요.\"\n" +
                      "{speech} 무레나: \"그때 배웠습니다. 제가 파는 게 정보가 아니라 사람이라는 걸요. 그 뒤로는 잘 잡니다. 알고 파니까요.\"\n"
                    : ""),
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("당신도 공범이다 (명성 +5)", _ => { AddRep(5f);
                    return "무레나: \"예. 그렇습니다. 부정할 줄 아셨습니까? 저는 제가 뭔지 압니다. 모르는 건 그쪽이시고요.\""; }),
                ("아무 말도 하지 않는다", _ => "무레나: \"…현명하십니다.\" 그가 증서를 내려놓았다. 오늘 몫이다"),
                ("왜 지금 말하나", _ => {
                    AddKeepsake("단서", "무레나의 부탁", "무레나: \"제가 말한 게 아닙니다. 그 사람이 말한 거지요.\"\n\n" +
                        "무레나: \"저는 다만… 당신이 그 사람을 내치지 않기를 바랍니다. 이상하게 들리시겠지만요.\"\n\n" +
                        "무레나: \"그 사람이 저기 있어야, 제가 20년 전에 한 일이 아직 안 끝난 게 되거든요.\"", "중개인 무레나");
                    return "무레나: \"…당신이 그 사람을 내치지 않기를 바랍니다. 그 사람이 저기 있어야, 제가 20년 전에 한 일이 아직 안 끝난 게 되거든요.\""; }) } },

        // ── 3막 C0 「검은 인장의 시험대」 — 한 번도 팔지 않은 자에게만 ──
        // 깨끗한 길에는 보호막이 없다. 그걸 몸으로 겪어야 C2의 제안이 실제로 매력적으로 들린다.
        new EvtTemplate { Id = "story_c_trial", Icon = "{swords}", Title = "검은 인장의 시험대", NeedsFighter = false,
            Body = _ => "검은 인장은 직접 움직이지 않았다. 대신 라이벌 루두스의 라니스타가 공개 석상에서 당신 이름을 불렀다.\n" +
                "{speech} 라니스타: \"저 집은 깨끗하다고들 하더군요. 빚을 지고도 안 판다고요.\"\n" +
                "{speech} 라니스타: \"그럼 증명해 보시지요. 우리 간판과 당신 간판, 다음 라운드에. 조건은 없습니다. 다만—\"\n" +
                "{speech} 라니스타: \"—지면, 그건 깨끗해서가 아니라 그냥 약해서 안 판 거라고 말하겠습니다. 온 도시에요.\"\n" +
                "그날부터 막사 분위기가 달라졌다. 아무도 말하지 않았지만 다들 알고 있었다.\n" +
                "밤에 연습장 불이 꺼지지 않았다. " +
                (_storyFlags.Contains("cato_exiled")
                    ? "{speech} 테아: \"붕대를 미리 준비해 뒀습니다. 이런 경기는 티가 나거든요.\""
                    : "{speech} 카토: \"…이게 저쪽 방식입니다. 자기들 손엔 피 안 묻히고요. 20년 전에도 이랬습니다. 먼저 외롭게 만들고, 그 다음에 제안하지요.\""),
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("받는다 (명성 +30 · 인기 +20)", _ => {
                    Flag("trial_won"); AddRep(30f);
                    var f = MyFirst; if (f != null) f.Popularity += 20f;
                    return "명성 +30 · 인기 +20 — 관중석이 조용했다가, 한 박자 늦게 터졌다. 안 팔고 이겼다. 그날 밤 막사에서 검 닦는 소리가 유난히 오래 났다. 아무도 시키지 않았다"; }),
                ("묵살한다 (명성 −8 · 빚 +30%)", _ => {
                    Flag("trial_lost"); _ludusRep = MathF.Max(0f, _ludusRep - 8f);
                    DebtTxn("도발을 묵살한 값 — 시장의 소문", MathF.Round(_debt * 0.3f));
                    return "명성 −8 · 빚 +30% — 다음 주 시장에서 우리 이야기가 돌았다. 좋은 쪽은 아니었다. 그 주에 증서가 두 통 왔다"; }) } },

        // ── 3막 C1 「값이 매겨진 밤」 — 간판을 사겠다는 제안 ──
        new EvtTemplate { Id = "story_c_offer", Icon = "{coin}", Title = "값이 매겨진 밤", NeedsFighter = false,
            Body = _ => "라이벌 가문이 사절을 보냈다.\n" +
                $"{{speech}} 사절: \"{MyFirst?.Name ?? "당신 간판"}을(를) 사겠습니다. 빚을 전부 덮고도 남는 값입니다.\"\n" +
                (_storyFlags.Contains("exec_refused")
                    ? "{speech} 루킬리우스: \"제가 다리를 놨습니다. 저번엔 아쉬웠으니, 이번엔 잘해 드리려고요.\"\n" : "") +
                (_storyFlags.Contains("cato_exiled")
                    ? "조언해 줄 사람이 없다. 테아가 문가에 서 있지만 아무 말도 하지 않는다. 그녀는 원래 그런다."
                    : "{speech} 카토: \"…팔지 마십시오. 저는 이런 말 할 자격 없습니다. 그런데 이 방에서 저 말고 할 사람이 없군요.\""),
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("판다 (빚 청산 · 명성 −10)", _ => {
                    Flag("sold_star");
                    float wipe = _debt; if (wipe > 0f) DebtTxn("간판을 판 값 — 전액 청산", -wipe);
                    _gold += 120f; _ludusRep = MathF.Max(0f, _ludusRep - 10f);
                    return "빚이 사라졌다. 골드 +120, 명성 −10 — 막사에 침상이 하나 더 비었다. 담요는 다음 날 아침에도 개켜져 있었다"; }),
                ("거절한다 (명성 +12)", _ => {
                    Flag("kept_star"); AddRep(12f);
                    return "명성 +12 — 사절: \"…아버님 아들이시군요. 칭찬으로 드리는 말은 아닙니다.\""; }) } },

        // ── 1막 비트② 「첫 원한」 — 지목 격파 도전 ──
        new EvtTemplate { Id = "story_challenge", Icon = "{swords}", Title = "지목 격파", NeedsFighter = false,
            Body = _ => {
                var t = _storyCtx != null ? _cast.FirstOrDefault(g => g.Id == _storyCtx) : null;
                t ??= ChallengeTarget();
                return $"광장에 방이 붙었다 — {(t != null ? $"{LudusNameOf(t.LudusId)}의 {t.Name}" : "경쟁 검투소의 간판")}이(가) 당신의 루두스를 콕 집어 도전을 걸었다.\n" +
                    $"{{speech}} 카토: \"받아들이면 저 녀석은 오늘을 잊지 않을 겁니다. …당신도요. 원한이란 그렇게 시작되지요.\"";
            },
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("도전을 받는다 (전시 경기 — 출전자는 라니스타이 고른다)", _ => {
                    var t = _storyCtx != null ? _cast.FirstOrDefault(g => g.Id == _storyCtx) : null;
                    t ??= ChallengeTarget();
                    var f = MyFirst;
                    if (t == null || f == null) return "성사되지 못했다 — 상대가 없다";
                    _pendingProposalOpp = t.Id; _proposalExec = false;
                    _ledger.DeepenGrudge(f.Id, t.Id, 18f);
                    return $"{{swords}} 도전 성사 — {t.Name}과(와)의 전시 경기. 출전자를 정하라 (관계 원장이 움직이기 시작했다)"; }),
                ("무시한다 (명성 +5)", _ => { AddRep(5f); return "명성 +5 — 방은 비에 젖어 떨어졌다. 하지만 군중은 기억한다"; }) } },

        // ── 1막 비트③ 「시대의 소음」 — 반란 지수 점화 ──
        new EvtTemplate { Id = "story_unrest", Icon = "{flame}", Title = "시대의 소음", NeedsFighter = false,
            Body = _ => "남쪽 훈련소에서 모리튜리들이 탈주했다는 소문이 시장을 돈다. 노예 값이 뛰고, 관중은 어쩐지 더 피에 굶주렸다.\n" +
                "{speech} 카토: \"모리튜리 값이 오릅니다. 군중은 더 목말라하고요. …시대가 흔들리면 모래가 제일 먼저 압니다.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("루두스 경비를 강화한다 (골드 −40)", _ => { var pay = SpendOrDebt(40f); _unrest = Math.Clamp(_unrest + 6f, 0f, 100f);
                    return $"{pay} — 담장을 올리고 자물쇠를 바꿨다. 소문은 소문으로 남기를"; }),
                ("소문은 소문일 뿐 (무시)", _ => { _unrest = Math.Clamp(_unrest + 12f, 0f, 100f);
                    return "카토: \"…그러길 바랍니다.\" (거리의 공기가 달라지고 있다)"; }) } },

        // ── 1막 비트④ 「진상의 반쪽」 ──
        new EvtTemplate { Id = "story_clue", Icon = "{candle}", Title = "진상의 반쪽", NeedsFighter = false,
            Body = _ => "몰락한 전직 라니스타가 술에 절어 당신의 소매를 붙잡는다. 가이우스의 이름에 그의 눈이 또렷해진다.\n" +
                "{speech} 전직 라니스타: \"가이우스가 거부한 그 경기… 돈을 댄 건 무레나가 아니야. 그 위야. 1부를 쥔 손. …1부에 올라가면 알게 될 거요.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("술값을 쥐여주고 더 캐묻는다 (골드 −20)", _ => { var pay = SpendOrDebt(20f);
                    AddClue("전직 라니스타 — \"그 경기의 돈줄은 1부를 쥔 손. 콜로세움 위의 관람석.\"");
                    return $"{pay} — 단서를 유품함에 적어 두었다. 승격하라. 답은 1부에 있다"; }),
                ("취객의 헛소리로 넘긴다", _ => { AddClue("취객의 말 — \"1부에 올라가면 알게 될 거요.\"");
                    return "돌아서는 등 뒤로 그가 외쳤다 — \"가이우스도 그렇게 웃었지!\""; }) } },

        // ── 1막 비트⑤ 「승격 결전 전야」 ──
        new EvtTemplate { Id = "story_showdown", Icon = "{candle}", Title = "결전 전야", NeedsFighter = false,
            Body = _ => {
                var f = MyNextFighter();
                string who = f?.Name ?? "당신의 모리튜리";
                string tail = _fixChoice == "accept" ? "지난번엔 현명하셨지요. 이번에도 그러시길."
                            : _fixChoice == "refuse" ? "지난번의 그 고집, 오늘은 접어 두시지요."
                            : "우리가 없으면 이 경기장은 일주일도 못 갑니다.";
                return $"등불도 없이 무레나가 문가에 서 있다. 검은 인장의 봉랍이 촛농처럼 흘러내린다.\n" +
                    $"{{speech}} 무레나: \"{who}의 다음 경기 — 져 주십시오. 가이우스처럼 굴지 마시고. {tail}\"";
            },
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("고개를 끄덕인다 (다음 경기를 던지면 골드 +200)", _ => {
                    var f = MyNextFighter(); if (f == null) return "던질 경기가 없다 — 무레나가 혀를 차며 사라졌다";
                    _fixFighterId = f.Id; _fixReward = 200f;
                    return $"{{candle}} 검은 거래 — {f.Name}이(가) 다음 경기를 던져야 한다. \"영광은 다음에도 살 수 있습니다.\""; }),
                ("문을 닫는다 (명성 +15)", _ => { AddRep(15f);
                    return "명성 +15 — 문틈으로 목소리가 스몄다. \"가이우스도 꼭 그렇게 문을 닫았지요.\""; }) } },

        // ── 종막 「라니스타가 되는 의식」 ──
        new EvtTemplate { Id = "story_finale", Icon = "{ludus}", Title = "모래에게 배우다", NeedsFighter = false,
            Body = _ => FinaleBody(),
            Choices = _storyFlags.Contains("cato_exiled")
                ? new (string, Func<Gladiator?, string>)[] {
                    ("도끼의 날을 닦는다", _ => "부탁받은 대로 했다. 날은 여전히 녹슬지 않았다 — 이제 그것을 닦는 사람은 당신이다"),
                    ("모래를 한 줌 움켜쥔다", _ => "따뜻했다. 오늘 흘린 피의 온기가 아직 남아 있었다 — 이제 전부 당신의 것이다") }
                : new (string, Func<Gladiator?, string>)[] {
                    ("모래를 한 줌 움켜쥔다", _ => "따뜻했다. 오늘 흘린 피의 온기가 아직 남아 있었다 — 이제 전부 당신의 것이다"),
                    ("카토에게 고개를 숙인다", _ => _storyFlags.Contains("cato_kept")
                        ? "카토: \"…라니스타가 교관에게 고개를 숙이면 안 됩니다. 다시는요.\" 그의 눈가가 잠깐 붉었다"
                        : "카토: \"…라니스타가 교관에게 고개를 숙이면 안 됩니다. 다시는요.\" 그는 아직 당신의 대답을 기다리고 있다") } },

        // ═══ 후일담 「황제의 게임」 — 가이우스 미스터리의 나머지 반쪽 (명성 4단계+ 독립 게이트) ═══

        // E1 「총애의 초대」 — 명문 루두스가 되자 궁정이 눈을 돌린다
        new EvtTemplate { Id = "story_e1", Icon = "{eye}", Title = "총애의 초대", NeedsFighter = false,
            Body = _ => "특명 두루마리에 낯선 봉랍이 하나 더 붙어 있다. 황제의 것이 아니다.\n" +
                "{speech} 카토: \"요즘 궁정에서 당신 이름이 오르내린답니다. …가이우스도 딱 이만큼 올라갔을 때부터 그랬지요.\"\n" +
                "{speech} 무레나: \"축하드립니다, 라니스타. 이제 '총애'가 무엇인지 배우실 차례군요. 그건 하사되는 게 아닙니다 — 팔리는 거지요.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("\"총애를 파는 자가 누구지?\"", _ => {
                    AddClue("무레나 — \"총애는 하사되는 게 아니라 팔리는 것. 파는 손은 콜로세움 꼭대기에 있다.\"");
                    return "무레나는 웃기만 했다 — \"더 올라오십시오. 그 높이에선 보입니다.\" (유품함에 기록)"; }),
                ("무레나를 내쫓는다 (명성 +5)", _ => { AddRep(5f);
                    AddClue("낯선 봉랍 — 황제의 것이 아닌 인장이 특명에 붙어 있었다.");
                    return "명성 +5 — 문가에서 그가 말했다. \"가이우스도 처음엔 내쫓았습니다.\" (유품함에 기록)"; }) } },

        // E2 「특명 뒤의 손」 — 특명의 진짜 발신인
        new EvtTemplate { Id = "story_e2", Icon = "{candle}", Title = "특명 뒤의 손", NeedsFighter = false,
            Body = _ => "특명을 완수한 밤, 무레나가 축하주도 없이 찾아왔다. 처음 보는 얼굴을 하고서.\n" +
                "{speech} 무레나: \"특명이 어디서 오는지 아십니까? 황제는 서명만 합니다. 문장을 고르는 건 — 총애를 파는 손이지요.\"\n" +
                "{speech} 무레나: \"가이우스가 거절한 그 경기. 돈은 검은 인장에서 나오지 않았습니다. 그 손에서 나왔지요. 나는… 심부름꾼이었을 뿐입니다.\"\n" +
                "{speech} 카토: \"…반쪽이 맞춰졌군요. 나머지 반쪽은 콜로세움 꼭대기에 있습니다. 올라가면, 만나게 될 겁니다.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("\"왜 이제 와서 말하지?\"", _ => {
                    AddClue("무레나 — \"심부름꾼도 늙습니다. 그리고 늙은 심부름꾼은… 빚을 갚고 싶어지지요.\"");
                    return "무레나: \"심부름꾼도 늙습니다. 늙은 심부름꾼은 빚을 갚고 싶어지지요.\" — 그의 눈이 처음으로 웃지 않았다"; }),
                ("\"심부름꾼도 공범이다\" (명성 +8)", _ => { AddRep(8f);
                    AddClue("특명 뒤의 손 — 황제는 서명만 한다. 문장을 고르는 건 총애를 파는 손.");
                    return "명성 +8 — 무레나는 부정하지 않았다. \"그래서 갚으러 온 겁니다.\""; }) } },

        // E3 「콜로세움의 귀빈석」 — 진실, 그리고 라니스타의 선택 (사실은 닫히되 선택의 무게는 남는다)
        new EvtTemplate { Id = "story_e3", Icon = "{ludus}", Title = "콜로세움의 귀빈석", NeedsFighter = false,
            Body = _ => "콜로세움 최상단, 자줏빛 차양 아래. 이름을 대지 않는 원로원의 손이 당신을 초대했다.\n" +
                "{speech} 원로원의 손: \"가이우스는 좋은 라니스타였습니다. 셈이 나빴을 뿐. 경기 하나의 값과 목숨 하나의 값을 저울질하지 못했지요.\"\n" +
                "{speech} 원로원의 손: \"독은 빠르고, 조용하고, 정확합니다. 셈이 빠른 사람은 그걸 마실 일이 없지요. — 당신은 셈이 빠르다고 들었습니다.\"\n" +
                "{speech} 카토: \"(낮게) …저 자입니다. 이제 당신이 정하십시오. 가이우스의 아들로서가 아니라 — 라니스타로서.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("모든 것을 폭로한다 (명성 +40 {glory}+10 · 검은 인장의 보복 리스크)", _ => {
                    AddRep(40f); AddGlory(10f);
                    AddClue("진실 — 가이우스는 독살당했다. 명령한 손은 총애를 파는 원로원, 무레나는 전달자였다.");
                    var rng = new SimRandom(_worldSeed ^ 0xE3E3_0001UL);
                    if (rng.Roll(0.6f) && _gold > 40f)
                    {
                        float loss = MathF.Round(_gold * 0.25f); _gold -= loss;
                        _story.Add((0, "story", $"{{flame}} 보복 — 그날 밤 루두스 창고에 불이 났다 (골드 −{loss:F0})"));
                        return $"{{ludus}} 폭로 — 원로원이 뒤집혔다. 명성 +40 {{glory}}+10. …그리고 그날 밤, 창고에 불이 났다 (골드 −{loss:F0})";
                    }
                    return "{ludus} 폭로 — 원로원이 뒤집혔다. 명성 +40 {glory}+10. 카토: \"가이우스가 오늘 밤은 편히 자겠군요.\""; }),
                ("침묵을 판다 (골드 +250)", _ => { _gold += 250f;
                    AddClue("진실 — 가이우스는 독살당했다. 나는 그 값을 받았다.");
                    return "{coin} 입막음의 값 +250 — 카토는 그날 밤 훈련장 갈퀴질을 평소보다 오래 했다"; }),
                ("그 손을 잡는다 (총애 +2 · 명성 −20)", _ => { _favor += 2; _ludusRep = MathF.Max(0f, _ludusRep - 20f);
                    AddClue("진실 — 가이우스는 독살당했다. 나는 그 손을 잡았다.");
                    return "{eye} 총애 +2, 명성 −20 — \"현명하시군요. 가이우스보다.\" 어디서 들어본 말이었다"; }) } },
    };

    // ── B7 「20년」 — 카토의 자백 조립 ──
    // 사실은 하나다. 바뀌는 것은 ① 진입(플레이어가 얼마나 파고들었나 = 조각 수)
    //                        ② 선행 보강(무엇을 물었나 = 플래그)
    //                        ③ 본문의 태도(어떤 라니스타였나 = 에토스) — 자수 / 보고 / 고백.

    /// <summary>진입 — 벽을 얼마나 채웠는지가 첫 마디를 정한다.</summary>
    private string ConfessOpening() => ClueCount switch
    {
        <= 2 => "{speech} 카토: \"…제가 처음부터 말해야겠군요. 아무것도 모르시는 것 같아서.\"\n",
        <= 5 => "{speech} 카토: \"어디까지 아십니까. …아니, 됐습니다. 제가 다 말하겠습니다.\"\n",
        _    => "{speech} 카토: \"벽을 채우셨더군요.\"\n" +
                "{speech} 카토: \"…그럼 이제 마지막 한 줄만 남았습니다. 그건 제 입에서 나와야 하는 거고요.\"\n",
    };

    /// <summary>선행 보강 — 무엇을 물었고 무엇을 안 물었는지가 그의 부채감을 바꾼다.</summary>
    private string ConfessPreamble()
    {
        var s = "";
        s += _storyFlags.Contains("asked_about_gaius")
            ? "{speech} 카토: \"전에 언젠가 대답하겠다고 했지요. 오늘 하겠습니다. 더 미루면 영영 못 할 것 같아서요.\"\n"
            : "{speech} 카토: \"물어보지 않으셔서 편했습니다. …비겁하게도요.\"\n";
        if (_storyFlags.Contains("axe_asked"))
            s += "{speech} 카토: \"저 도끼가 누구 것이냐고 물으셨지요. 그때 대답 안 했습니다. 오늘 하겠습니다.\"\n";
        if (_storyFlags.Contains("both_stood") || _storyFlags.Contains("asked_why"))
            s += "{speech} 카토: \"당신은 사람한테 이유를 묻는 분이더군요. 그러면 저한테도 물으실 겁니다. 그전에 제가 말하겠습니다.\"\n";
        if (_storyFlags.Contains("overworked"))
            s += "{speech} 카토: \"약제사가 '한 시즌만'이라는 말을 들었다고 하더군요. 저도 그 말을 들은 적이 있습니다. 20년 전에요.\"\n";
        return s;
    }

    /// <summary>본문 — 냉혹(자수) / 중립(보고) / 인간(고백). 오르쿠스 사건의 사실관계는 셋 다 동일하다.</summary>
    private string ConfessBody() => EthosBand switch
    {
        // 자수 — 그는 앉지 않는다. 나갈 거리를 재고 있다.
        "cold" =>
            "그는 앉지 않았다. 문 쪽에 서 있다. 나갈 수 있는 거리를 재고 있는 사람의 자세다.\n" +
            "{speech} 카토: \"오르쿠스라고, 아실 겁니다. 제가 길렀습니다. 열다섯에 받아서 128승까지 봤습니다.\"\n" +
            "{speech} 카토: \"그 사람은 8년을 거절했습니다. 거절하고도 계속 이겼고요. 저쪽에서는 그게 제일 곤란했지요.\"\n" +
            "{speech} 카토: \"그해 봄에 갈비뼈가 부러졌습니다. 조각이 폐를 찔렀고요. 안 붙었습니다.\"\n" +
            "{speech} 카토: \"서너 라운드 넘어가면 심장이 터지는 몸이 됐습니다. 저만 알았고요.\"\n" +
            "{speech} 카토: \"그래서 제가 팔았습니다. 무레나한테요. 그 숨 이야기를요.\"\n" +
            "그는 여기서 당신 얼굴을 봤다. 그리고 본 것을 후회하는 표정을 지었다.\n" +
            "{speech} 카토: \"…변명은 안 하겠습니다. 어차피 안 들으실 테니까요.\"\n" +
            "{speech} 카토: \"위에서는 그걸로 대진표를 짰습니다. 죽일 놈을 붙인 게 아닙니다. 안 죽일 놈을 붙였지요. 탑방패요. 하루 종일 도망만 다니는 놈으로요.\"\n" +
            "{speech} 카토: \"숨이 차서 알아서 죽으라고요. 아무 손에도 피가 안 묻습니다.\"\n" +
            "{speech} 카토: \"대진표가 나온 날 밤, 저는 알았습니다. 가이우스 방 앞까지 갔고요. 말 안 했습니다.\"\n" +
            "{speech} 카토: \"그 사람은 이겼습니다. 그 방패를 기어이 쪼개고 132승째를요. 그리고 모래 위에 앉아서 안 일어났습니다.\"\n" +
            "{speech} 카토: \"저쪽은 두 번 졌습니다. 돈을 잃었고, 자기들 손으로 전설을 만들었고요. 그래서 그날 밤 주인을 지운 겁니다. 이야기할 사람이 없으면 전설은 소문이 되니까요.\"\n" +
            "그가 잠시 말을 멈춘다. 그리고 아주 조용히, 다른 이야기를 하듯이 말한다.\n" +
            "{speech} 카토: \"…요즘 당신을 보면 가이우스가 안 떠오릅니다.\"\n" +
            "{speech} 카토: \"그를 죽인 자들이 떠오릅니다.\"\n" +
            "{speech} 카토: \"그게 제 잘못인지, 당신 잘못인지, 아니면 원래 이렇게 되는 건지 저는 모르겠습니다.\"\n" +
            "{speech} 카토: \"…아마 원래 이렇게 되는 걸 겁니다. 그래서 말씀드린 겁니다. 처분하십시오.\"\n" +
            "그는 처분받을 각오가 아니라, 처분받는 게 마땅하다는 확신으로 서 있었다.",

        // 고백 — 그는 앉자마자 고개를 숙인다. 용서받을까 봐 겁내면서 말한다.
        "warm" =>
            "카토가 먼저 찾아온 것은 두 번째다. 그는 앉았고, 앉자마자 고개를 숙였다. 이 사람이 고개를 숙이는 것을 본 적이 없다.\n" +
            "{speech} 카토: \"…미리 말씀드리겠습니다. 저는 지금 겁이 납니다.\"\n" +
            "{speech} 카토: \"당신이 화내실까 봐가 아니라, …용서하실까 봐 겁이 납니다.\"\n" +
            "{speech} 카토: \"오르쿠스라고, 아실 겁니다. 제가 길렀습니다. 열다섯에 받아서 128승까지 봤습니다.\"\n" +
            "그가 벽 쪽을 본다. 도끼가 걸려 있다. 날은 여전히 녹슬지 않았다.\n" +
            "{speech} 카토: \"그 사람은 8년 동안 거절했습니다. 거절해도 살아남는 사람이 하나 있으면 다른 루두스들이 계산을 시작하거든요. 저쪽에서는 그게 제일 곤란했습니다.\"\n" +
            "{speech} 카토: \"그해 봄 대련에서 갈비뼈가 부러졌습니다. 다들 붙었다고 생각했지요. 저도 그런 줄 알았고요.\"\n" +
            "{speech} 카토: \"그런데 그 녀석이 뱉은 침에 검은 게 섞여 있었습니다. 한 방울요. 모래에 떨어져서 금방 사라졌습니다.\"\n" +
            "{speech} 카토: \"그 뒤로 숨소리를 들었습니다. 쇳소리가 났습니다. 아주 작게요. 가슴 안에서 뭔가 새는 소리였습니다.\"\n" +
            "{speech} 카토: \"조각이 폐를 찌른 겁니다. 서너 라운드가 넘어가면 심장이 터질 몸이었습니다. 저만 알았습니다. 교관이니까요 — 쇳소리만 들어도 압니다.\"\n" +
            "그가 손등으로 자기 가슴을 한 번 짚었다. 자기 것이 아닌 아픔을 짚는 손이었다.\n" +
            "{speech} 카토: \"…그 녀석이 왜 늘 빨리 끝냈는지 아십니까. 백서른둘 중에 아흔일곱을 KO로 끝냈습니다.\"\n" +
            "{speech} 카토: \"관중은 그게 자비인 줄 알았습니다. 아니었습니다. 길어지면 자기가 죽으니까 그런 겁니다.\"\n" +
            "{speech} 카토: \"그 사람 기록은 전적이 아니라 진단서였습니다. 온 도시가 그걸 매주 읽으면서 아무도 못 읽었지요.\"\n" +
            "{speech} 카토: \"말하지 말라더군요. 자기가 은퇴하면 이 루두스가 넘어가고 안에 있던 서른이 흩어진다고요.\"\n" +
            "{speech} 카토: \"'한 시즌만 더 버티면 저 아이들이 산다'고 했습니다. …저는 그 말을 20년 동안 생각합니다. 거짓말이었으면 좋겠어서요.\"\n" +
            "{speech} 카토: \"그때 무레나가 왔습니다. 은퇴시키라고, 값은 치르겠다고요.\"\n" +
            "{speech} 카토: \"그래서 팔았습니다. 그 숨 이야기를요.\"\n" +
            "{speech} 카토: \"…돈 때문이 아닙니다. 저는 그걸로 그 사람을 모래에서 끌어내고 싶었습니다.\"\n" +
            "{speech} 카토: \"저는 그 문장을 20년 동안 연습했습니다. 아직도 변명처럼 들리는군요.\"\n" +
            "{speech} 카토: \"…\"\n" +
            "{speech} 카토: \"위에서는 다르게 썼습니다. 호흡이 짧은 걸 알면, 누굴 붙여야 못 버티는지도 아니까요.\"\n" +
            "{speech} 카토: \"창을 든 귀신을 보낸 게 아닙니다.\"\n" +
            "{speech} 카토: \"하루 종일 도망만 다니면서 시간만 끄는 탑방패잡이를 대진표에 올렸습니다. 싸우지도 않는 놈으로요.\"\n" +
            "{speech} 카토: \"…숨이 차서 알아서 죽으라고요.\"\n" +
            "그 말이 무슨 뜻인지 이해하는 데 잠깐 걸렸다. 이해하고 나니 속이 식었다. 아무도 그를 죽이지 않는다. 그냥 오래 서 있게 하면 된다.\n" +
            "{speech} 카토: \"그게 저쪽 방식입니다. 누구 손에도 피가 안 묻습니다. 검시관이 볼 수 있는 건 심장뿐이고요.\"\n" +
            "{speech} 카토: \"기록에는 '경기 중 급사'라고 적힙니다. 그게 답니다.\"\n" +
            "{speech} 카토: \"대진표가 나온 날 밤, 저는 그걸 알았습니다. 상대 이름 옆에 '스쿠타투스'라고 적힌 걸 보는 순간에요.\"\n" +
            "{speech} 카토: \"가이우스한테 말할 수 있었습니다. 그 방 문 앞까지 갔습니다.\"\n" +
            "{speech} 카토: \"…말하려면 제가 팔았다고 해야 했습니다. 그래서 안 했습니다.\"\n" +
            "그는 여기서 오래 멈췄다. 20년치의 멈춤이었다.\n" +
            "{speech} 카토: \"…그런데 그 사람이 이겼습니다.\"\n" +
            "{speech} 카토: \"저쪽이 계산 안 한 게 하나 있었거든요. 사람이 죽기 직전에 얼마나 세지는지요.\"\n" +
            "{speech} 카토: \"심장이 터지기 직전이었을 겁니다. 저는 그 얼굴을 봤습니다. 그런데 도끼를 놓지 않더군요.\"\n" +
            "{speech} 카토: \"그 무거운 방패를 기어이 쪼갰습니다. 132승째였습니다.\"\n" +
            "{speech} 카토: \"상대가 실려 나가고, 그 사람은 모래 위에 앉아서 안 일어났습니다. 심장이었다고 하더군요.\"\n" +
            "{speech} 카토: \"…맞습니다. 심장이었습니다. 저쪽이 적어 넣은 그대로요.\"\n" +
            "{speech} 카토: \"관중이 그 이름을 불렀습니다. 그날 처음으로 온 경기장이 한 사람 이름만 불렀습니다.\"\n" +
            "{speech} 카토: \"…그래서 그날 밤 가이우스가 죽었습니다.\"\n" +
            "{speech} 카토: \"저쪽은 두 번 졌거든요. 돈을 잃었고, 자기들 손으로 전설을 만들었고요. 거절하고 죽는 건 순교입니다. 순교는 옮습니다.\"\n" +
            "{speech} 카토: \"그래서 주인을 지운 겁니다. 그 승리를 계속 이야기할 사람이 없어지면, 전설은 소문이 되니까요.\"\n" +
            "당신은 전설 명부에 적힌 문장을 떠올린다 — 「검은 인장에 맞서다 사라졌다는 소문만 남았다.」 소문으로 만드는 것이 목적이었다.\n" +
            "{speech} 카토: \"제가 죽인 게 아닙니다. 그건 압니다.\"\n" +
            "{speech} 카토: \"그런데 제가 그날 밤 문을 열었으면 둘 다 살았을 수도 있습니다. 그것도 압니다.\"\n" +
            "{speech} 카토: \"저는 20년 동안 이 루두스를 안 떠났습니다. 갚으려고 남은 게 아닙니다. …갚을 수 없다는 걸 매일 확인하려고 남았습니다.\"\n" +
            "{speech} 카토: \"제가 '도끼는 욕심 때문에 죽는다'고 말하는 걸 들으셨을 겁니다. 여러 번요.\"\n" +
            "{speech} 카토: \"그 사람은 욕심으로 죽지 않았습니다. 저는 그걸 알면서 20년 동안 그렇게 말했습니다. …그게 제일 부끄럽습니다.\"\n" +
            "그가 고개를 들었다. 눈이 붉었는데, 울고 있지는 않았다. 우는 법을 잊은 사람의 눈이었다.\n" +
            "{speech} 카토: \"당신은 제가 본 라니스타 중에 제일 이상한 분입니다. 아이들 이름을 다 아시더군요.\"\n" +
            "{speech} 카토: \"…그래서 말씀드릴 수 있었습니다. 이제 처분하십시오.\"",

        // 보고 — 감정을 담을 게 남아 있지 않은 사람의 문장.
        _ =>
            "카토가 먼저 찾아온 것은 두 번째다. 그는 앉는 데 오래 걸렸다.\n" +
            "{speech} 카토: \"오르쿠스라고, 아실 겁니다. 제가 길렀습니다. 열다섯에 받아서요.\"\n" +
            "{speech} 카토: \"길게 안 하겠습니다. 길게 하면 변명이 되니까요.\"\n" +
            "{speech} 카토: \"그 사람 폐가 망가졌습니다. 서너 라운드 넘어가면 죽는 몸이었고요. 저만 알았습니다. 저는 그걸 무레나한테 팔았습니다.\"\n" +
            "{speech} 카토: \"…돈 때문이 아니었습니다. 그 사람을 모래에서 끌어내고 싶었습니다. '이 자는 끝났다, 물러나게 하자' — 그런 말이 나오길 바랐지요.\"\n" +
            "{speech} 카토: \"위에서는 대진표로 썼습니다. 시간만 끄는 놈을 붙였고요. 숨이 차서 알아서 죽으라고요.\"\n" +
            "{speech} 카토: \"대진표가 나온 날 밤 저는 알았고, 가이우스한테 말 안 했습니다. 말하려면 제가 팔았다고 해야 했으니까요.\"\n" +
            "{speech} 카토: \"그 사람은 이겼습니다. 그리고 모래 위에서 죽었습니다. 그날 밤 가이우스도 죽었고요.\"\n" +
            "{speech} 카토: \"…이겁니다. 다입니다.\"\n" +
            "그는 감정을 담지 않았다. 담을 게 남아 있지 않은 사람의 문장이었다.\n" +
            "{speech} 카토: \"저는 20년 동안 안 떠났습니다. 갚으려고 남은 게 아닙니다. 갚을 수 없다는 걸 매일 확인하려고 남았습니다.\"\n" +
            "{speech} 카토: \"처분하십시오. 그게 제가 온 이유입니다.\"",
    };

    private string ConfessionBody() => ConfessOpening() + ConfessPreamble() + ConfessBody();

    /// <summary>종막 — 카토의 처분이 이 장면의 무게를 뒤집는다.
    /// 「내가 가르칠 수 있는 건 여기까지입니다」가 튜토리얼 종료 멘트에서 떠날 자격을 구하는 말이 된다.</summary>
    private string FinaleBody()
    {
        string epilogue = "\n각본은 여기서 끝난다. 콜로세움, 챔피언십 컵, 불멸의 루두스, 세대와 유산 — " +
            "그리고 아직 답을 얻지 못한 보관함의 질문들. 모래가 당신을 기억할 뿐.";

        if (_storyFlags.Contains("cato_exiled"))
            return "시즌의 먼지가 가라앉은 훈련장. 갈퀴가 벽에 기대어 있다. 아무도 그것을 들지 않았다.\n" +
                "벽에는 도끼 한 자루와 목검 하나가 그대로 걸려 있다. 20년 동안 누군가 매일 그 자리에 세워두던 것들이다.\n" +
                "…\n" +
                "품에서 유서가 만져진다 — 「모래는 정직하다. 그 위의 인간들이 문제일 뿐.」\n" +
                "이제 그 위에 서 있는 인간은 당신이다.\n" +
                "{speech} 테아: \"…날은 제가 닦고 있습니다. 부탁받은 건 아니고요.\"" + epilogue;

        if (_storyFlags.Contains("cato_unanswered"))
            return "시즌의 먼지가 가라앉은 훈련장. 카토가 갈퀴를 내려놓고 처음으로 당신을 정면으로 본다.\n" +
                "{speech} 카토: \"내가 가르칠 수 있는 건 여기까지입니다.\"\n…\n" +
                "{speech} 카토: \"이제부터는… 당신도 모래에게 배우게 될 겁니다.\"\n" +
                "{speech} 카토: \"…아직 대답을 안 주셨습니다. 재촉하는 건 아닙니다. 기다리는 것도 제 몫이니까요.\"\n" +
                "그는 갈퀴를 다시 집어 들었다. 내일도 여기 있겠다는 뜻이다." + epilogue;

        if (_storyFlags.Contains("cato_kept"))
            return "시즌의 먼지가 가라앉은 훈련장. 카토가 포도주를 두 잔 따른다.\n" +
                "그리고 이번에는 — 붓지 않는다. 두 잔 다 탁자에 놓는다.\n" +
                "{speech} 카토: \"내가 가르칠 수 있는 건 여기까지입니다.\"\n…\n" +
                "{speech} 카토: \"이제부터는… 당신도 모래에게 배우게 될 겁니다.\"\n" +
                "{speech} 카토: \"저는 계속 여기 있겠습니다. 벌 받으러가 아니라, …이제는 그냥 일하러요.\"\n" +
                "그가 잔을 든다. 20년 만에 처음으로, 두 잔이 다 채워진 채였다. 벽의 도끼는 여전히 그 자리에 있다." + epilogue;

        // 자백 전에 종막에 닿은 커리어(승격이 빨랐거나 각본을 앞질렀을 때) — 원래의 담백한 종막
        return "시즌의 먼지가 가라앉은 훈련장. 카토가 갈퀴를 내려놓고 처음으로 당신을 정면으로 본다.\n" +
            "{speech} 카토: \"내가 가르칠 수 있는 건 여기까지입니다.\"\n…\n" +
            "{speech} 카토: \"이제부터는… 당신도 모래에게 배우게 될 겁니다.\"" + epilogue;
    }

    /// <summary>자백을 보관함에 편철 — 처분과 무관하게 진실은 남는다. 기억의 벽의 마지막 한 줄.</summary>
    private void ArchiveConfession()
    {
        Flag("clue_confess");
        AddKeepsake("메모", "카토의 자백 — 20년",
            "오르쿠스. 카토가 열다섯에 받아 기른 도끼. 8년을 거절하고도 계속 이겼다.\n\n" +
            "그해 봄, 부러진 갈비뼈 조각이 폐를 찔렀다. 서너 라운드가 넘어가면 심장이 터지는 몸.\n" +
            "아는 사람은 카토뿐이었다 — 뱉은 침에 섞인 검은 핏방울, 숨 쉴 때마다 새는 쇳소리.\n\n" +
            "카토는 그를 모래에서 끌어내려고 그 사실을 팔았다. 은퇴 명분이 만들어지길 바라고.\n" +
            "위에서는 그것으로 대진표를 짰다. 죽일 자가 아니라 「안 죽일 자」 — 하루 종일 물러나기만 하는 탑방패.\n" +
            "싸우지 않고 시간만 끌어, 스스로 숨 막혀 죽게 하는 대진이었다. 누구 손에도 피가 묻지 않는다.\n\n" +
            "대진표가 나온 밤, 카토는 알았고 말하지 않았다. 말하려면 자기가 팔았다고 해야 했으므로.\n\n" +
            "오르쿠스는 이겼다. 방패를 쪼개고 132승째를 가져갔다. 그리고 모래 위에 앉아 일어나지 않았다.\n" +
            "공식 사인은 심장. 사실이었다.\n\n" +
            "그날 밤 가이우스가 죽었다. 전설에는 주인이 필요하고, 주인을 지우면 전설은 소문이 되므로.\n\n" +
            "— 「검은 인장에 맞서다 사라졌다는 소문만 남았다.」",
            "교관 카토");
    }

    /// <summary>기존 "발신 — 내용" 형식의 단서를 보관함 문서로 승격(발신처로 타입 추정). 구 AddClue 호출부 전부 재사용.</summary>
    private void AddClue(string clue)
    {
        int dash = clue.IndexOf(" — ", StringComparison.Ordinal);
        string from = dash > 0 ? clue[..dash].Trim() : "";
        string body = dash > 0 ? clue[(dash + 3)..].Trim() : clue.Trim();
        string type = from.Contains("유서") ? "유서"
                    : from.Contains("서신") || from.Contains("봉랍") || from.Contains("인장") ? "서신"
                    : from.Contains("메모") ? "메모" : "단서";
        AddKeepsake(type, from.Length > 0 ? from : "단서", body, from);
    }

    /// <summary>보관함에 유품 문서를 편철(제목+본문 중복 제거). 서신·유서·메모 등 타입별로 클라가 전용 디자인 렌더.</summary>
    private void AddKeepsake(string type, string title, string body, string from)
    {
        if (_keepsakes.Any(k => k.Title == title && k.Body == body)) return;
        _keepsakes.Add(new KeepsakeRec(type, title, body, from, RomanDate()));
    }

    /// <summary>선대 가이우스의 유서 — 미스터리의 씨앗(게임 전체를 떠도는 유령 §v0.2). 서막 S0에서 편철.</summary>
    private void AddGaiusWill() => AddKeepsake("유서", "가이우스의 유서",
        "내 아들에게.\n\n모래는 정직하다 — 그 위의 인간들이 문제일 뿐이다.\n" +
        "나는 이길 수 없는 경기를 이겼고, 그 값이 무엇인지 안다. 너는 나처럼 굴지 마라. …아니, 어쩌면 너도 나처럼 굴겠지. 그게 우리 핏줄이니.\n" +
        "무너진 루두스와 빚을 남겨 미안하다. 카토를 믿어라 — 그는 나보다 정직한 사람이다.\n" +
        "그리고, 검은 인장을 든 자가 찾아오거든… 문을 열어주더라도, 마음은 열지 마라.\n\n— 가이우스",
        "선대 라니스타 가이우스");

    // ── 서막 튜토리얼 힌트 (카토의 조언 — 서버가 계산, 클라이언트는 표시만) ──

    private string? StoryHint()
    {
        if (_playerless || _storyStage == "chronicle") return null;
        if (!_cast.Any(g => g.IsPlayer))
            return "{speech} 카토: \"돈이 없습니다. 무기와 기질만 보고 골라야 해요 — 나머지는 모래가 가르칠 겁니다.\" → 영입 탭에서 뽑기로 첫 모리튜리를 들이십시오";
        if (!SeasonActive)
            return "{speech} 카토: \"무엇을 시킬지가 아니라, 무엇을 하게 둘지를 정하는 겁니다.\" → 훈련을 분배하고 [다음 경기 ▶]로 시즌을 여십시오";
        if (_cast.Where(g => g.IsPlayer).All(g => g.CW + g.CL + g.CD == 0))
            return "{speech} 카토: \"당신은 저 아이를 조종할 수 없습니다. 다만 방향을 일러줄 수는 있지요.\" → 내 경기 관전 중 {pause} 일시정지로 전술을 바꿀 수 있습니다(경기당 2회)";
        // 1막 — 무레나가 아직 안 왔다면 빚이 먼저 말한다(첫 상환일 예고, [13a] A3)
        if (_storyStage == "act1" && !_storyBeats.Contains("a3") && _debt > 0f)
            return "{speech} 카토: \"상환일이 옵니다. 그때는 사람이 직접 옵니다 — 지금까지는 숫자만 왔지만요.\"";
        return null;   // 이후는 이벤트가 이야기한다
    }

    private CampaignDoc? BuildCampaignDoc() => _playerless ? null
        : new CampaignDoc(_storyStage, _storyBeats.OrderBy(x => x).ToArray(), StoryHint(),
            ClueIds.Where(_storyFlags.Contains).ToArray(),   // 기억의 벽 — 획득한 조각(순서 고정)
            _storyFlags.Contains("cato_exiled") ? "exiled" : "cato");

    /// <summary>보관함 탭 문서 목록 — 최신 편철이 위로.</summary>
    private List<KeepsakeRec>? BuildKeepsakes() => _playerless || _keepsakes.Count == 0 ? null
        : Enumerable.Reverse(_keepsakes).ToList();

    /// <summary>서신 이벤트 열람 시 그 편지를 보관함에 편철(발신 문서 = 서신). ChooseEventJson에서 호출.</summary>
    private void ArchiveLetter(string from, string title, string body) => AddKeepsake("서신", title, body, from);

    /// <summary>letter Kind 이벤트의 발신처 — 보관함 편철·편지 봉투 라벨용.</summary>
    private string LetterSender(string id) => id switch
    {
        "story_house_gold" => $"{CtxLudusName}의 사절",
        "story_house_blood" => CtxLudusName,
        "rival_letter" => ActiveRivalLudi.FirstOrDefault(r => r.Persona == "blood").Name ?? "경쟁 검투소",
        _ => "발신 미상",
    };

    // ── 반란 지수 (살아있는 세계 — 사이클, 엔딩 없음) ──

    private int UnrestStageIdx => _unrest >= 75f ? 3 : _unrest >= 50f ? 2 : _unrest >= 25f ? 1 : 0;
    private static readonly (string Name, string Icon)[] UnrestStages =
        { ("평온", "{dove}"), ("소문", "{speech}"), ("폭동", "{flame}"), ("검문", "{shield}") };
    /// <summary>경기 수입 배수 — 시국 불안 = 세금·검문(최대 −10%).</summary>
    private float UnrestIncomeMult => 1f - _unrest / 100f * 0.10f;
    /// <summary>흥행 배수 — 불안한 시대일수록 군중은 피에 목마르다(최대 +15%).</summary>
    private float UnrestHypeMult => 1f + _unrest / 100f * 0.15f;

    /// <summary>시즌 틱 — 고조↔소강의 줄다리기(결정론). 폭동·검문 단계는 스스로 가라앉는 압력을 받는다(사이클).</summary>
    private void TickUnrest()
    {
        var rng = new SimRandom(_worldSeed ^ 0x0E57_2026UL + (ulong)_seasonNo * 41UL);
        int before = UnrestStageIdx;
        float drift = rng.NextFloat01() * 20f - 6f;              // −6 ~ +14 (완만한 상승 편향)
        if (_unrest >= 75f) drift -= 9f;                          // 검문 = 진압 국면(소강 회귀)
        else if (_unrest >= 50f) drift -= 3f;
        _unrest = Math.Clamp(_unrest + drift, 0f, 100f);
        int after = UnrestStageIdx;
        if (after != before && !_playerless)
        {
            var s = UnrestStages[after];
            _story.Add((_rounds + 1, "unrest", after > before
                ? $"{s.Icon} 시대의 소음 — 거리가 「{s.Name}」 국면으로 접어들었다 (반란 지수 {_unrest:F0})"
                : $"{s.Icon} 소강 — 거리가 「{s.Name}」 국면으로 가라앉았다 (반란 지수 {_unrest:F0})"));
        }
        // 검문 국면: 흥행세 — 금고의 5%를 뜯긴다 (진행 차단 없음, 비용으로만)
        if (after == 3 && !_playerless && _gold > 20f)
        {
            float tax = MathF.Round(_gold * 0.05f);
            _gold -= tax;
            _story.Add((_rounds + 1, "unrest", $"{{shield}} 검문 강화 — 총독부가 흥행세를 걷어갔다 (골드 −{tax:F0})"));
        }
    }

    private UnrestDoc? BuildUnrestDoc()
    {
        if (_playerless) return null;
        var s = UnrestStages[UnrestStageIdx];
        var fx = new List<string>();
        if (UnrestStageIdx >= 1) fx.Add($"수입 −{_unrest * 0.10f:F0}% · 흥행 +{_unrest * 0.15f:F0}%");
        if (UnrestStageIdx >= 2) fx.Add("노예 부족 — 뽑기 후보 2명");
        if (UnrestStageIdx >= 3) fx.Add("시즌말 흥행세 5%");
        return new UnrestDoc((int)MathF.Round(_unrest), s.Name, s.Icon, fx.Count > 0 ? string.Join(" · ", fx) : "거리가 조용하다");
    }

    // ── 전설 (창세 시드 + 명전 승격 — 세계에 과거와 척도를 준다) ──

    private static readonly (string Name, string Epithet, string Weapon, string Personality, string Record, string Fate)[] LegendPool =
    {
        ("오르쿠스",  "마지막 도끼 챔피언", "AXE",        "PER_CRUEL",       "132승 9패 · KO 97",  "검은 인장에 맞서다 사라졌다는 소문만 남았다"),
        ("아퀼라",    "핏빛 매",           "SPEAR",      "PER_CALM",        "98승 21패 · KO 40",  "목검(루디스)을 받고 자유민으로 늙어 죽었다"),
        ("탈로스",    "성벽",              "SHIELD",     "PER_WARY",        "76승 12패 · KO 18",  "7시즌 무패 뒤, 무릎이 먼저 은퇴를 고했다"),
        ("세르페나",  "모래뱀",            "WHIP",       "PER_OPPORTUNIST", "88승 30패 · KO 31",  "관중이 등을 돌린 밤, 종적을 감췄다"),
        ("브렌누스",  "갈리아의 낫",       "GREATSWORD", "PER_BOLD",        "67승 18패 · KO 50",  "처형전에서 웃으며 죽었다"),
        ("아켈레온",  "두 개의 번개",      "DUALBLADES", "PER_SHOWMAN",     "104승 40패 · KO 62", "은퇴 후 흥행주가 되어 더 부자가 됐다"),
        ("파비우스",  "물러서지 않는 자",  "HAMMER",     "PER_HONORABLE",   "71승 9패 · KO 33",   "황제의 사면으로 검을 놓았다"),
        ("느바르",    "누미디아의 폭풍",   "SWORD",      "PER_RECKLESS",    "59승 22패 · KO 44",  "반란의 소문과 함께 이름이 지워졌다"),
    };

    /// <summary>창세 전설 4명 — worldSeed 선발(커리어마다 다른 과거). 구세이브에도 소급 시드.
    /// 캠페인 커리어는 오르쿠스를 고정 편성한다 — [13a] 척추가 그의 마지막 경기 위에 서 있으므로
    /// 무작위로 빠지면 카토의 자백이 참조할 과거가 사라진다. 나머지 3명만 무작위(회차 변주 유지).</summary>
    private void SeedLegends()
    {
        if (_legends.Count > 0) return;
        var rng = new SimRandom(_worldSeed ^ 0x1E6E_D05EUL);
        bool campaign = !_playerless;
        var picked = LegendPool.OrderBy(_ => rng.NextUInt64())
                               .Where(l => !campaign || l.Name != "오르쿠스")
                               .Take(campaign ? 3 : 4).ToList();
        if (campaign) picked.Insert(0, LegendPool.First(l => l.Name == "오르쿠스"));
        foreach (var l in picked)
            _legends.Add(new LegendRec(l.Name, l.Epithet, l.Weapon.Replace("WPN_", ""), l.Personality,
                l.Record, l.Fate, 655 + (int)(rng.NextUInt64() % 20), "seed"));
    }

    /// <summary>명전 승격 — 은퇴 2시즌+ 지난 명성 60+ 헌액자를 전설로(시즌당 1명). 세대가 지날수록 역사가 두꺼워진다.</summary>
    private void PromoteLegends()
    {
        if (_playerless) return;
        var cand = _hall.Where(h => h.Fame >= 60f && h.RetiredSeason <= _seasonNo - 2
                                    && _legends.All(l => l.Name != h.Name))
                        .OrderByDescending(h => h.Fame).FirstOrDefault();
        if (cand == null) return;
        string ep = cand.Career.Contains("전사") ? "모래로 돌아간 자"
                  : cand.Games > 0 && cand.CKoW >= cand.Games / 2 ? "처형인"
                  : cand.BestStreak >= 8 ? "불패"
                  : cand.IsPlayer ? "루두스의 별" : "옛 시대의 왕";
        _legends.Add(new LegendRec(cand.Name, ep, cand.Weapon, "", cand.Career + $" · KO {cand.CKoW}",
            cand.IsPlayer ? "이 루두스에서 검을 놓았다" : "모래가 그 이름을 기억한다", 680 + cand.RetiredSeason, "hof"));
        _story.Add((_rounds + 1, "legend", $"{{ludus}} 전설이 되다 — {cand.Name} 「{ep}」, 이제 세대가 그를 척도로 삼는다"));
    }

    // ── 카토 코멘터리 (상시 어시스턴트 — 매 경기 한 줄 평) ──

    /// <summary>경기 후 카토의 한 줄 — 내 경기 100% · AI전 35%. 규칙 기반(MatchSummary 사실 → 텍스트 풀), 신규 연산 없음.
    /// [13a] 카토를 내친 커리어(cato_exiled)에서는 화자가 바뀐다 — 내 경기는 테아(몸·값), AI전은 루킬리우스(흥행·시장).
    /// 페널티는 수치가 아니라 어휘의 상실이다: 이 세계가 검투사를 몸값과 흥행 항목으로만 말하게 된다.</summary>
    private string? CatoComment(Gladiator A, Gladiator B, int winner, string reason, bool myMatch)
    {
        if (_playerless) return null;
        var rng = new SimRandom(SeasonSeed ^ 0xCA70_CA70UL + (ulong)_matchIdx * 71UL);
        if (!myMatch && !rng.Roll(0.35f)) return null;
        if (_storyFlags.Contains("cato_exiled")) return ExiledComment(A, B, winner, reason, myMatch, rng);

        if (winner < 0) return "모래도 가끔은 답을 미룹니다.";
        var (win, lose) = winner == 0 ? (A, B) : (B, A);
        bool ko = reason == "KO";

        // 전설 참조 — 닮은 현역(무기 일치 + 성격 일치 또는 KO 기질), 통산 8승+, 시즌 2회 한도, 25%
        if (_legendRefs < 2 && win.CW >= 8 && rng.Roll(0.25f))
        {
            var match = _legends.FirstOrDefault(l => l.Weapon == win.WeaponId.Replace("WPN_", "")
                && (l.Personality == "" || l.Personality == win.PersonalityId));
            if (match != null && match.Name != win.Name)
            {
                _legendRefs++;
                // [13a] 조각 4 — 오르쿠스를 참조하는 순간, 플레이어는 피해자의 프로필을 읽게 된다(그때는 모른다)
                if (match.Name == "오르쿠스" && !_storyFlags.Contains("clue_legend"))
                {
                    Flag("clue_legend");
                    AddKeepsake("단서", "오르쿠스의 기록", $"「{match.Epithet}」 — {match.Record}\n{match.Fate}\n\n" +
                        "카토가 경기평 끝에 흘리듯 덧붙인 이름이다.\n" +
                        "승수의 대부분이 KO다. 빨리 끝내는 선수였다는 뜻이다.", "전설 명부");
                }
                return $"이 녀석… 옛날의 {match.Name}을(를) 닮았군요. 「{match.Epithet}」 — {match.Record}. …끝이 어땠는지는, 기록을 찾아보시지요.";
            }
        }

        var pool = new List<string>();
        if (_lastUpset) pool.Add("모래는 명성을 읽지 못합니다. 오늘 그걸 증명했군요.");
        // [13a] 조각 3 — 20년치 자기기만. 오르쿠스는 욕심으로 죽지 않았고, 카토는 그걸 알면서 이렇게 말해왔다.
        if (ko && lose.WeaponId == "WPN_AXE")
        {
            if (!_storyFlags.Contains("clue_commentary"))
            {
                Flag("clue_commentary");
                AddKeepsake("메모", "카토의 말버릇", "\"도끼는 늘 욕심 때문에 죽지요.\"\n\n" +
                    "도끼가 쓰러진 경기마다 그는 같은 말을 한다. 늘 같은 어조로, 늘 같은 자리에서.\n" +
                    "누구 이야기인지는 말한 적이 없다.", "교관 카토");
            }
            pool.Add("도끼는 늘 욕심 때문에 죽지요.");
        }
        if (ko && lose.PersonalityId == "PER_RECKLESS") pool.Add("저 성미로는 언젠가 이런 밤이 옵니다. 오늘이 그 밤이었을 뿐.");
        if (ko) pool.Add($"깨끗한 끝이었습니다. 군중은 {win.Name}의 이름을 오래 기억할 겁니다.");
        if (!ko) pool.Add("판정은 군중의 몫입니다만 — 모래는 누가 더 절실했는지 압니다.");
        if (win.WeaponId is "WPN_SPEAR" or "WPN_WHIP") pool.Add($"{win.Name}은(는) 발이 빠르군요. 거리가 곧 목숨인 무기라서요.");
        if (win.WeaponId is "WPN_HAMMER" or "WPN_GREATSWORD") pool.Add("무거운 무기는 서두르지 않습니다. 오늘은 기다림이 이겼군요.");
        if (win.WeaponId == "WPN_SHIELD") pool.Add("방패가 이기는 밤은 조용합니다. 군중은 몰라도, 저는 압니다.");
        if (win.WeaponId == "WPN_DUALBLADES") pool.Add("쌍검은 숨 쉴 틈을 안 줍니다. 진 쪽은 아직도 못 셌을 겁니다 — 몇 대 맞았는지.");
        if (lose.Fatigue >= 60) pool.Add("지친 검은 무딥니다. 오늘은 몸이 진 겁니다.");
        if (win.Streak >= 4) pool.Add($"{win.Name}… 챔피언이 될 놈입니다. 기억해 두십시오.");
        // 사망(극적 운명)으로 이미 원장에서 지워진 선수를 재조회하면 유령 엔트리가 생긴다(Get=지연 생성) — 생존자만
        if (_cast.Any(g => g.Id == lose.Id) && _cast.Any(g => g.Id == win.Id)
            && _ledger.Get(lose.Id, win.Id).Classify(lose.PersonalityId) is RelationType.Nemesis)
            pool.Add("원한은 오래갑니다. 저 둘은 또 만날 겁니다.");
        if (myMatch && lose.IsPlayer && !win.IsPlayer) pool.Add("괜찮습니다. 오늘 죽은 건 우리가 아니라, 기대였을 뿐입니다.");
        if (pool.Count == 0) pool.Add(rng.Roll(0.5f)
            ? "오늘은 거리 싸움이었습니다. 반 보 차이가 전부였지요."
            : "모래는 오늘도 정직했습니다. 그 위의 인간들이 문제일 뿐.");
        pool.Add("좋은 경기였습니다. 내일이면 아무도 기억 못 하겠지만 — 그게 모래지요.");
        return pool[(int)(rng.NextUInt64() % (ulong)pool.Count)];
    }

    /// <summary>카토가 없는 세계의 경기평 — 테아는 몸을 세고, 루킬리우스는 표를 센다.
    /// 전설 참조는 하지 않는다: 이 세계에서 과거를 기억하던 목소리가 사라졌기 때문이다.</summary>
    private string ExiledComment(Gladiator A, Gladiator B, int winner, string reason, bool myMatch, SimRandom rng)
    {
        if (winner < 0) return myMatch ? "무승부입니다. 둘 다 살아서 돌아왔고요. 저는 그거면 됩니다." : "무승부네요. 표는 이미 팔렸으니 상관없습니다만.";
        var (win, lose) = winner == 0 ? (A, B) : (B, A);
        bool ko = reason == "KO";
        var pool = new List<string>();
        if (myMatch)
        {
            // 테아 — 판단하지 않고 센다. 승패보다 몸이 먼저다.
            if (lose.IsPlayer && ko) pool.Add("실려 왔습니다. 갈비뼈를 봐야겠습니다.");
            else if (lose.IsPlayer) pool.Add("졌습니다. 큰 데는 없고요. 그건 다행입니다.");
            if (win.IsPlayer && win.Fatigue >= 60) pool.Add("이겼습니다. 숨소리는 안 좋고요. 오늘은 세지 않겠습니다.");
            if (win.IsPlayer) pool.Add("오늘은 안 다쳤습니다. 다음엔 다칠 겁니다.");
            if (win.IsPlayer && win.CW <= 3) pool.Add($"{win.Name}, 손에 물집이 잡혔더군요. 아직 쥐는 법을 모르는 겁니다.");
            pool.Add("붕대가 두 롤 줄었습니다. 적어두겠습니다.");
            pool.Add("오늘은 늦었습니다. 가서 주무십시오.");
        }
        else
        {
            // 루킬리우스 — 끝까지 이름을 묻지 않는다. 그게 캐릭터의 전부다.
            if (ko) pool.Add("깔끔했어요! 이런 게 팔립니다. 이름이 뭐랬죠? 아, 안 물어봤네요.");
            if (!ko) pool.Add("판정까지 갔네요. 판정은 표가 안 팔립니다. 다음엔 좀 짧게 부탁드려요.");
            if (win.WeaponId == "WPN_SHIELD") pool.Add("방패는 인기가 없습니다. 안 죽거든요.");
            pool.Add("군중이 오늘 좀 조용했습니다. 날씨 탓이겠죠. 아마도요.");
            pool.Add("표가 잘 나갑니다! 아, 당신 몫은 아니고요. 제 몫이요.");
        }
        return pool[(int)(rng.NextUInt64() % (ulong)pool.Count)];
    }
}
