using Morituri.Sim.Data;

namespace Morituri.Sim.Match;

/// <summary>
/// 연속 상태의 한 프레임 — "어디 있고, 얼마 남았고, 무슨 자세인가".
/// SimEvent(이산 "무슨 일이 있었나")가 담지 않는 위치/포즈를 뷰어가 그릴 수 있게 하는 프레젠테이션 투영.
/// 도메인 이벤트 스트림과 분리돼 있어 MatchRecord(schemaVer=1)를 오염시키지 않는다(문서[1] 6장).
/// </summary>
public readonly record struct ReplayFrame(
    float Time,
    float Ax, float Ay, float Bx, float By,   // 원형 핏 2D 좌표 (중심 0,0). B: 2D-lite
    float HpPctA, float HpPctB,
    float StamPctA, float StamPctB,
    FighterState StateA, FighterState StateB,
    MotionKind KindA, MotionKind KindB,       // 현재 스윙 종류(강/약) — 스프라이트가 강공/약공 포즈를 고른다
    float Crowd = 0f,                         // 군중게이지 −100~+100 (+=A편) — 뷰어 미터·함성용 (문서[10])
    int BleedA = 0, int BleedB = 0);          // 출혈 스택(0~3) — 뷰어 🩸 표시 (문서[7]§2)
