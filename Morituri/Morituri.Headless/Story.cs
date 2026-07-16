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
    private string? _fixChoice;                             // 서막 S5의 선택(accept/refuse) — 무레나 대사 변주
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
    private sealed record CampaignDoc(string Stage, string[] Beats, string? Hint);
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

        // 서막 — 장례(S0) → 첫 방문자(S5, 첫 영입 후)
        if (!_storyBeats.Contains("s0")) return SpawnStory("story_s0", "s0");
        if (!_storyBeats.Contains("s5") && _cast.Any(g => g.IsPlayer)) return SpawnStory("story_s5", "s5");
        if (_storyStage == "prologue")
        {
            if (!SeasonActive) return false;
            _storyStage = "act1";   // 개막과 함께 1막 「모래 위의 도시」
        }
        if (!afterMatch || !SeasonActive) return false;

        // 1막 비트 ① 세 가문 — 각자의 방식으로 신참을 "환영" (개성 타입 바인딩 — 어느 가문이 뽑혀도 동작)
        foreach (var r in ActiveRivalLudi)
            if (!_storyBeats.Contains("house_" + r.Id))
                return SpawnStory("story_house_" + r.Persona, "house_" + r.Id, ctx: r.Id);
        // ② 첫 원한 — 지목 격파 도전장
        if (!_storyBeats.Contains("b2") && _cast.Any(g => !g.IsPlayer) && _cast.Any(g => g.IsPlayer))
            return SpawnStory("story_challenge", "b2", ctx: ChallengeTarget()?.Id);
        // ③ 시대의 소음 — 반란 지수 점화
        if (!_storyBeats.Contains("b3")) return SpawnStory("story_unrest", "b3");
        // ④ 진상의 반쪽 — 승격 = 서사 동기화
        if (!_storyBeats.Contains("b4")) return SpawnStory("story_clue", "b4");
        // ⑤ 승격 결전 전야 — 검은 인장의 요구 (다음 내 경기가 남아 있을 때)
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

    private bool SpawnStory(string templateId, string beatId, string? ctx = null)
    {
        _storyBeats.Add(beatId);
        _storyCtx = ctx;
        _pendingEventId = templateId;
        _pendingEventFighter = null;
        return true;
    }

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

        // ── 서막 S5 「첫 방문자」 — 무레나, 검은 인장 ──
        new EvtTemplate { Id = "story_s5", Icon = "{candle}", Title = "검은 인장의 방문", NeedsFighter = false,
            Body = _ => "해질녘, 값비싼 토가를 입은 사내가 빚 증서 뭉치를 탁자에 올려놓는다. 인장은 검다.\n" +
                "{speech} 무레나: \"가이우스의 후계자시군. 빚은 피를 가리지 않습니다. …허나 갚을 방법은 여러 가지지요.\"\n" +
                "{speech} 무레나: \"당신 모리튜리가 적당한 날에 적당히 져 주기만 하면 됩니다. 우린 아무도 죽이지 않아요 — 당신들이 돈 때문에 죽이는 거죠. 우린 그저 결과를 정리할 뿐.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("고개를 끄덕인다 (다음 경기를 던지면 골드 +120)", _ => {
                    var f = MyNextFighter(); if (f == null) return "던질 모리튜리가 없다 — 무레나가 코웃음 치며 떠났다";
                    _fixFighterId = f.Id; _fixReward = 120f; _fixChoice = "accept";
                    AddClue("무레나 — \"우리가 없으면 이 경기장은 일주일도 못 갑니다.\"");
                    return $"{{candle}} 검은 거래 — {f.Name}이(가) 다음 경기를 던져야 한다. 무레나: \"현명하시군요. 가이우스보다는.\""; }),
                ("증서를 밀어낸다 (명성 +10)", _ => {
                    AddRep(10f); _fixChoice = "refuse";
                    AddClue("무레나 — \"당신 아버지는 끝까지 거절했습니다. 딱 한 번만 져 주면 됐는데. …그는 이겼고, 뭘 얻었습니까?\"");
                    return "명성 +10 — 무레나: \"당신 아버지랑 똑같군. 그 고집이 어디로 이어졌는지는… 아실 텐데.\""; }) } },

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
                $"{{speech}} 서신: \"무너진 루두스의 애송이가 모래를 밟는다지. 네 모리튜리들은 우리 모래 위에선 한 합도 못 버틴다. 얼마나 버티는지 구경이나 하마.\"",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("공개 답신으로 맞받아친다 (인기 +6, 원한)", g => {
                    var f = MyFirst; if (f == null) return "답할 모리튜리가 없다";
                    f.Popularity += 6f;
                    var t = PickGrudgeTarget(f, _storyCtx);
                    if (t != null) { _ledger.DeepenGrudge(f.Id, t.Id, 22f); return $"{f.Name} 인기 +6 — {t.Name}({LudusNameOf(t.LudusId)})에게 원한을 새겼다"; }
                    return $"{f.Name} 인기 +6"; }),
                ("침묵한다 (명성 +6)", _ => { AddRep(6f); return "명성 +6 — 카토: \"짖는 개는 물지 않습니다. 무는 개는 조용하지요.\""; }) } },

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
            Body = _ => "시즌의 먼지가 가라앉은 훈련장. 카토가 갈퀴를 내려놓고 처음으로 당신을 정면으로 본다.\n" +
                "{speech} 카토: \"내가 가르칠 수 있는 건 여기까지입니다.\"\n…\n" +
                "{speech} 카토: \"이제부터는… 당신도 모래에게 배우게 될 겁니다.\"\n" +
                "각본은 여기서 끝난다. 콜로세움, 챔피언십 컵, 불멸의 루두스, 세대와 유산 — 그리고 아직 답을 얻지 못한 유품함의 질문들. 모래가 당신을 기억할 뿐.",
            Choices = new (string, Func<Gladiator?, string>)[] {
                ("모래를 한 줌 움켜쥔다", _ => "따뜻했다. 오늘 흘린 피의 온기가 아직 남아 있었다 — 이제 전부 당신의 것이다"),
                ("카토에게 고개를 숙인다", _ => "카토: \"…라니스타가 교관에게 고개를 숙이면 안 됩니다. 다시는요.\" 그의 눈가가 잠깐 붉었다") } },

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
        return null;   // 이후는 이벤트가 이야기한다
    }

    private CampaignDoc? BuildCampaignDoc() => _playerless ? null
        : new CampaignDoc(_storyStage, _storyBeats.OrderBy(x => x).ToArray(), StoryHint());

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

    /// <summary>창세 전설 4명 — worldSeed 선발(커리어마다 다른 과거). 구세이브에도 소급 시드.</summary>
    private void SeedLegends()
    {
        if (_legends.Count > 0) return;
        var rng = new SimRandom(_worldSeed ^ 0x1E6E_D05EUL);
        foreach (var l in LegendPool.OrderBy(_ => rng.NextUInt64()).Take(4))
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

    /// <summary>경기 후 카토의 한 줄 — 내 경기 100% · AI전 35%. 규칙 기반(MatchSummary 사실 → 텍스트 풀), 신규 연산 없음.</summary>
    private string? CatoComment(Gladiator A, Gladiator B, int winner, string reason, bool myMatch)
    {
        if (_playerless) return null;
        var rng = new SimRandom(SeasonSeed ^ 0xCA70_CA70UL + (ulong)_matchIdx * 71UL);
        if (!myMatch && !rng.Roll(0.35f)) return null;

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
                return $"이 녀석… 옛날의 {match.Name}을(를) 닮았군요. 「{match.Epithet}」 — {match.Record}. …끝이 어땠는지는, 기록을 찾아보시지요.";
            }
        }

        var pool = new List<string>();
        if (_lastUpset) pool.Add("모래는 명성을 읽지 못합니다. 오늘 그걸 증명했군요.");
        if (ko && lose.WeaponId == "WPN_AXE") pool.Add("도끼는 늘 욕심 때문에 죽지요.");
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
}
