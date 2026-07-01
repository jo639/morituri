# MORITURI — [2] 역사 DB · ERD 설계서 (v0.1)
**상태:** 신규 (2026-06-29). 로드맵[0] **Phase 3 착수 조건** 문서. 코드 미착수 — 이 문서가 스키마를 확정하면 시즌/역사 엔진 구현.
**한 줄:** 검투사가 세대를 이어 싸우는 **영속 세계의 데이터 모델**. 우리가 만든 감정(T10)·관계(T11)·천부(StatGen)·특성(T09)이 여러 경기·시즌에 걸쳐 *누적·이월*되는 무대의 스키마.
**전제 문서:** [1] 아키텍처(Sim/Meta 분리·직렬화), [10]§8 Fame 훅, [11] 천부/계급, [현황] 구현 상태.

---

## 0. 설계 원칙

1. **Sim은 무상태·무영속 (원칙 A 유지).** 역사 DB는 **Meta 레이어**가 소유한다. `Morituri.Sim`은 이 스키마를 모른다 — Meta가 `MatchResult`/`MatchRecord`를 *구독*해 적재만.
2. **결정론이 저장을 가볍게 한다 (원칙 B).** 한 경기의 진실은 **(seed + 양 선수 스냅샷)**. 이벤트 스트림 전량은 저장하지 않고 **필요 시 재시뮬레이션으로 재생**한다(리플레이·검증). DB엔 요약(`MatchResult`) + 하이라이트 태그 + 재현 키만.
3. **★ 스냅샷으로 재현 (핵심 결정).** 검투사는 성장·노화한다 → "시드 + 선수 id"만으론 과거 경기를 재현 못 한다(그때 스탯이 지금과 다름). 그래서 **Match는 경기 시점의 `FighterSnapshot`**(스탯·무기·전술·성격·특성·주입된 감정·관계)을 저장한다. `seed + snapshotA + snapshotB` → 바이트 동일 재현.
4. **schemaVer 전면 (원칙: 영속 기록은 포맷 진화에도 과거를 읽는다).** 모든 최상위 엔티티에 `schemaVer`. (`MatchSerializer.SchemaVersion` 이미 존재.)
5. **저장 포맷 = JSON 문서 (Phase 3 v1).** `MatchRecord` JSON과 동형. 규모가 커지면(수만 경기) SQLite/문서DB로 이관 — ERD는 논리 모델이라 저장소 무관.
6. **ID 규칙 (문서[5]):** `<도메인>_<슬러그>` 문자열 (정수 ID 금지). 예: `GLA_<uuid8>`, `SEA_S03`, `MTCH_<uuid8>`.

---

## 1. 엔티티 관계도 (논리 ERD)

```
                         ┌───────────────┐
             ┌──────────►│   Gladiator   │◄──────────┐  (parent → child, 혈통 Phase 4)
             │           │  (검투사·영속) │           │
             │           └──┬───┬───┬────┘           │
      1  ┌───┴───┐          │   │   │        N  ┌─────┴──────┐
   ┌─────┤ Season├───N──────┘   │   └────N──────┤  Relation  │ (self→opp, 방향성 그래프)
   │ N   │ (시즌)│  Standing     │  Emotion      └────────────┘
   │     └───┬───┘  (시즌 전적)  │ (일시 상태·이월)
   │         │                  │
   │  N      ▼                  ▼  N
   │     ┌───────┐         ┌──────────┐
   └────►│ Match ├──N──────┤ Narrative│ (comeback·revenge·rivalry_final…)
         │(경기 )│  Event  │  Event   │
         └───┬───┘         └──────────┘
             │ 1:1 (재현 키)
             ▼
      seed + FighterSnapshot ×2   (→ 재시뮬레이션으로 이벤트 재생)

   Gladiator ─N─ GladiatorSkill (T12, 예약) · Gladiator ─1─ Fame (누적)
```

**카디널리티 요약:** Season 1—N Match · Season N—N Gladiator(Standing 경유) · Gladiator 1—N Match(선수 A/B로) · Gladiator N—N Gladiator(Relation, 방향성) · Gladiator 1—N Emotion(활성) · Match 1—N NarrativeEvent · Gladiator 1—N GladiatorSkill · Gladiator 1—1 Fame.

---

## 2. 엔티티 정의

### 2.1 Gladiator (검투사) — 영속 정체성 ★
현 `FighterDef` + `StatGen.Endowment` + 커리어 누적. **성장·노화로 스탯이 변하는 살아있는 레코드.**

| 컬럼 | 타입 | 설명 · 매핑 |
|---|---|---|
| id | string | `GLA_*` |
| name | string | 표시명 |
| talentGrade | enum | `StatGen.TalentGrade` (노예~카이사르) — 고정 |
| potentialGrade | enum | `StatGen.PotentialGrade` (잿불~태양) — 고정 |
| talentBudget / potentialBudget | float | 생성 시 버짓 / 성장 상한 |
| stats | Stats6 | 현재 6축(Atk/Def/HpMax/Spd/Aspd/Rct) — **성장·노화로 변동** |
| weaponId / tacticsId / personalityId | string | T01/T03/T05 |
| traitIds | string[] | T09 (`TraitGen` 산물, 20세 +1 등 추가 가능) |
| rankTier | enum | 계급 (T13, 생성 시 고정·특수 사건만 상승 — [11]§4) |
| level | int | 예약 (레벨 도입 시 슬롯 예산) |
| age / birthSeason | int | 나이(성장 곡선·노화 트리거) |
| status | enum | Active / Retired / Deceased |
| lineageParentId | string? | 혈통 (Phase 4) |
| careerRecord | Record | 통산 승/패/무/KO승/KO패 (Standing 집계의 통산판) |
| fameId | string | → Fame 1:1 |

> **성장/노화:** `stats`는 `potentialBudget` 상한으로 훈련·커리어에 따라 상승, 노년엔 RCT부터 하락([3]6.3·[11]). 성장 엔진 = T09 `GrowthMod`/`AgingMod` 훅 (미구현).

### 2.2 Match (경기 기록) ★
현 `MatchRecord` + `MatchResult` + [10]§8 관중 export를 영속화.

| 컬럼 | 타입 | 설명 · 매핑 |
|---|---|---|
| id | string | `MTCH_*` |
| schemaVer | int | `MatchSerializer.SchemaVersion` |
| seasonId / round | string / int | 소속 시즌·라운드 |
| seed | ulong | 재현 키 |
| fighterA / fighterB | **FighterSnapshot** | §2.3 — 경기 시점 선수 상태(재현 필수) |
| winner | int | 0 / 1 / -1 |
| reason | enum | KO / Judgement / Draw |
| durationSec | float | |
| scoreA / scoreB | float | 판정 점수 |
| statsA / statsB | MatchFighterStats | 가한피해·클린히트·다운·헛스윙·코너체류·MinHpPct·HpRemainPct·Taunted (현 레코드) |
| crowdControlA/B, spectacleA/B | float | 관중 장악도·스펙터클 ([10]§8 export — Fame 입력) |
| highlightTags | string[] | comeback / taunt_reversal / revenge / upset … (Narrative 파생) |

> **이벤트 스트림은 비저장** — `seed + fighterA/B 스냅샷`으로 `MatchSim.Run` 재실행 시 동일 이벤트 재생(원칙 B). 뷰어/검증은 이 재현을 쓴다. (원한다면 명경기만 이벤트 blob 캐시 — 선택.)

### 2.3 FighterSnapshot (경기 시점 선수 상태) ★ — 재현의 핵심
경기가 벌어진 순간의 **불변 스냅샷**. Gladiator는 변하므로 Match는 이걸 박제한다.

| 컬럼 | 타입 | 매핑 |
|---|---|---|
| gladiatorId | string | 원 선수 참조 |
| stats | Stats6 | 그때의 스탯 |
| weaponId / tacticsId / personalityId | string | |
| traitIds | string[] | |
| emotionIds | string[] | 그 경기에 주입된 감정 (T10) |
| relationToOpp | enum? | 상대에 대한 관계 (T11) |
| relationIntensity | float | |

→ 곧 현 `FighterDef`의 영속 대응(= `FighterDef` + gladiatorId). `MatchSim.Run(FighterDef, FighterDef, seed)`에 그대로 투입 가능.

### 2.4 Season / League (시즌) ★
| 컬럼 | 타입 | 설명 |
|---|---|---|
| id | string | `SEA_*` |
| name / index | string / int | |
| scheduleType | enum | RoundRobin / Ladder / Bracket (v1=RoundRobin) |
| participantIds | string[] | 로스터 |
| status | enum | Scheduled / InProgress / Finished |
| championId | string? | 종료 시 |
| startedAt / endedAt | timestamp | |

### 2.5 Standing (시즌 전적/순위)
| 컬럼 | 타입 | 설명 |
|---|---|---|
| (seasonId, gladiatorId) | PK | |
| wins / losses / draws / koWins | int | |
| points | int | 승점 (승 3·무 1 등, 규칙 데이터) |
| rank | int | 현재 순위 |
| streak | int | 연승(+)/연패(−) — 감정(자만/트라우마)·Fame 입력 |

### 2.6 Relation (관계) ★ — RelationLedger 영속화
현 `RelationState`(방향성 self→opp)의 영속 대응.

| 컬럼 | 타입 | 매핑 (`RelationData.cs`) |
|---|---|---|
| (selfId, oppId) | PK | 방향성 |
| affinity | float | −100~+100 |
| encounters / wins / losses / koLosses / closeGames | int | 누적 전적 |
| dominantType | enum? | `Classify` 파생 캐시 (원수/공포/라이벌…) |
| lastMatchId | string | 최근 대전 |

> `RelationLedger`가 이 테이블의 인메모리 뷰. 시즌 진행마다 `RecordMatch` → upsert.

### 2.7 Emotion (감정 인스턴스) ★ — 이월/감쇠
현 감정은 인매치 1경기 고정이었으나, Phase 3에서 **일시 상태가 다음 경기로 이월**된다(감정의 진짜 수명).

| 컬럼 | 타입 | 설명 |
|---|---|---|
| id | string | |
| gladiatorId | string | 보유자 |
| emotionId | enum | T10 (자신감/원한/트라우마…) |
| sourceMatchId | string | 생성 계기 |
| targetGladiatorId | string? | 원한·트라우마는 특정 상대 귀속 |
| createdSeasonMatchIdx | int | 감쇠 기준점 |
| decayRemaining | int | 남은 경기 수(또는 시즌) — `EmotionDef.DecaySec`의 경기 단위 대응 |

> **생성:** `EmotionGen.Roll`(경기 결과 → 감정, 확률 발생 ~15%). **소비:** 다음 경기의 `FighterSnapshot.emotionIds`로 주입. **감쇠:** N경기 후 소멸. **성격 변화(Phase 4):** 누적 감정 → 성격 드리프트 입력.

### 2.8 Fame (인기·명성) — [10]§8 훅 구체화
| 컬럼 | 타입 | 설명 |
|---|---|---|
| gladiatorId | string | 1:1 |
| fame | float | 통산 명성 (전당·드래프트 가치) |
| popularity | float | 현 인기 (최근 활약·관중 장악, 감쇠) |
| fanbase | int | 팬 수 (이벤트 매치 흥행) |
| fameLog | FameDelta[] | 증감 이력(연승·이변·도발승·관중장악·처형) |

> **입력:** Match의 `crowdControl/spectacle` + Standing `streak` + 이변(하위가 상위 격파) + highlightTags. `Fame += f(관중장악, 스펙터클, 승패, 이변)`. **소비:** 이벤트 매치 선정 가중·매치메이킹·계급 상승 후보·드래프트가.

### 2.9 NarrativeEvent (서사 이벤트) — Highlights 확장
현 `HighlightEntry`(comeback/taunt_reversal)를 세계 서사로 확장.

| 컬럼 | 타입 | 설명 |
|---|---|---|
| id / matchId | string | |
| kind | enum | comeback / taunt_reversal / **revenge**(복수 성공)·**rivalry_final**·**upset**(이변)·debut·retirement·death·rank_up·hall_of_fame |
| actorId / targetId | string | 주체·대상 |
| tags / description | string[] / string | 큐레이션·표시 |

> RelationLedger의 `RevengeCandidates`가 실제 복수로 이어지면 `revenge` 이벤트 발행 → Fame·서사. (관계 → 서사의 연결.)

### 2.10 예약 (컬럼만) — Phase 3~4 심화
- **GladiatorSkill (T12):** (gladiatorId, skillId, slotIndex). 스킬 엔진 미구현([6][7]).
- **RankTier (T13):** (tier, poolAccess, slotBudget). 계급 상승 이벤트.
- **Item (전설 아이템):** id, effect, 계급상승 트리거 — [6]§1.5.
- **Lineage (혈통, Phase 4):** parentId → childId, 상속 규칙.
- **TrainingLog (성장):** gladiatorId, season, statDelta — T09 GrowthMod.

---

## 3. 핵심 데이터 흐름 (한 경기 → 세계 갱신)

```
MatchSim.Run(snapA, snapB, seed)  →  MatchResult (+ 관중 export)
        │
        ▼  (Meta 레이어가 구독)
  ① Match 레코드 저장 (seed + 스냅샷 + 요약 + 태그)
  ② Standing 갱신 (승패·승점·streak)
  ③ RelationLedger.RecordMatch → Relation upsert (관계 누적)
  ④ EmotionGen.Roll(결과) → Emotion 인스턴스 생성(확률) → 다음 경기 이월
  ⑤ Fame 갱신 (관중장악·스펙터클·이변·streak)
  ⑥ NarrativeEvent 발행 (comeback/revenge/upset…) → 하이라이트·서사
```

> ①~⑥은 전부 **기존 산출물의 소비자**다(원칙 A: Sim 무변경). 감정 생성(`EmotionGen`)·관계 누적(`RelationLedger`)·관중 export([10])·명경기 태깅(`Highlights`)이 이미 존재 — Phase 3는 이들을 **영속 루프로 엮는 것**.

---

## 4. 구현 매핑 (기존 코드 재사용)

| ERD 엔티티 | 기존 코드 | Phase 3 작업 |
|---|---|---|
| FighterSnapshot | `FighterDef`(+gladiatorId) | 거의 그대로 |
| Match | `MatchSerializer.MatchRecord` + `MatchResult` | 스냅샷·시즌·태그 필드 추가 |
| Relation | `RelationLedger` / `RelationState` | 직렬화(로드/세이브) |
| Emotion | `EmotionGen` / `EmotionDef` | 이월·감쇠 루프 |
| Fame | [10]§8 export (crowdControl/spectacle) | 공식 확정 + 누적 |
| NarrativeEvent | `Highlights.HighlightEntry` | kind 확장 |
| Gladiator | `StatGen.Endowment` + `TraitGen` | 성장·노화·커리어 |

**신규 어셈블리 제안:** `Morituri.Meta`(순수 C#, Sim 참조 O·Unity 참조 X) — League/History/Persistence. 또는 Phase 3 v1은 Headless 내 `Season.cs`로 시작(현 `relations` 데모의 라운드로빈 확장)해 스키마 검증 후 분리.

---

## 5. 저장 전략

- **v1 (수천 경기):** JSON 문서 — `world.json`(Gladiator·Relation·Fame·Season·Standing) + `matches/*.json`(경기별 요약, 이벤트는 seed 재현). `MatchSerializer` 옵션 재사용.
- **v2 (수만+):** SQLite. ERD 테이블 그대로 매핑. Match.events는 여전히 비저장(seed 재현).
- **명경기 캐시(선택):** highlightTags 있는 경기만 이벤트 blob 저장 → 뷰어 즉시 재생(재시뮬 생략).

---

## 6. 미해결 결정 (구현 전 확정)

1. **감정 이월 수명 단위** — 경기 수 N vs 시즌. 감쇠율(현 `DecaySec`의 경기 단위 환산).
2. **성장/노화 곡선** — 매 경기 소량 vs 훈련 이벤트. 노화 시작 나이·RCT 하락률([11]§7).
3. **승점·순위 규칙** — 승 3/무 1? KO 보너스? 시즌 길이·스케줄(라운드로빈 vs 사다리).
4. **Fame 공식** — 관중장악·스펙터클·이변 가중치([10]§11-6).
5. **계급 상승 트리거** — 어떤 업적·아이템·전당이 +1 계급([6]§7, [11]§7-4).
6. **매치메이킹 권한 범위** — 플레이어가 짜는 대진 vs 자동 스케줄 + 개입(기획시안 디벨롭 2).
7. **재현 vs 스냅샷 비용** — 밸런스 상수(`BalanceConstants`)가 바뀌면 과거 재현이 달라짐 → 스냅샷에 상수 버전도 박제할지(schemaVer로 게이트).

---

## 7. Phase 3 착수 순서 (이 문서 확정 후)
```
P3-A  Season 엔진: 영속 로스터 + 라운드로빈 + 경기간 감정/관계 이월 + 순위 → verify: 시즌 리포트에 라이벌·복수극·챔피언 창발
P3-B  Fame/인기: crowd export → 명성 누적 → 이벤트 매치 가중 → verify: 이변·연승이 인기로 측정
P3-C  매치메이킹 권한: 플레이어 대진 편성(간접 개입 1호) → verify: 복수전·라이벌전을 플레이어가 성사
P3-D  저장/로드: world.json 영속 → verify: 세션 넘어 세계 지속
```
> 로드맵[0] Phase 3 착수 조건("문서[2] ERD") **충족**. 다음 = P3-A 시즌 엔진(승인 시).
