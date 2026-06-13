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
    FighterState StateA, FighterState StateB);
