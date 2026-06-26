# MORITURI — Phase 1 구현 (M1~M4-b)

> 전체 현재 상태는 레포 루트 [`MORITURI_현황.md`](../MORITURI_현황.md) 참조. 이 파일은 빌드·실행·구조 요약.

## 실행
```
dotnet run --project Morituri.Sim.Tests          # 테스트 62개 (오프라인 자체 러너)
dotnet run --project Morituri.Headless -- 500    # 배치 시뮬레이션 (매치업당 N경기)
dotnet run --project Morituri.Headless -c Release -- viewer "b:SPEAR/COUNTER/CALM:AXE/BRAWLER/CRUEL" 7
                                                 # 경기 1판 → viewer.json + http://localhost:5173/viewer.html
```
명령어 전체는 [뷰어 명령어 레퍼런스](../뷰어_명령어_레퍼런스.md), 현황 §2 참조.
NUnit 전환(개발 PC): Tests csproj의 PackageReference 주석 해제, NUNIT_SHIM/OutputType 제거,
NUnitShim.cs·Program.cs 삭제 → `dotnet test`. 테스트 코드 무변경.

## 구조 (문서[1] 아키텍처 준수)
```
Morituri.Sim/            순수 C# — UnityEngine 무의존 (원칙 A)
  Core/SimRandom.cs        xorshift64* 결정론 RNG (원칙 B)
  Data/                    T01~T09 (원칙 C: 매직넘버 0개)
    BalanceConstants.cs      T06 전역 상수
    WeaponDef.cs             T01 무기 8종
    TacticsData.cs           T02 모션 / T03 전술 10종 / Directive·ParamMod
    PersonalityData.cs       T04 트리거 / T05 성격 10종 / T07 검증 선수
    TraitData.cs             T09 특성 14종 + 생성 추첨(TraitGen)
    StatGen.cs               천부/잠재력 추첨 + 스탯 분배(Endowment)
    FighterStats.cs          6대 스탯 (Baseline 70 균일)
  Combat/CombatMath.cs     문서[4] 수식 (순수 함수, 난수 주입)
  Events/SimEvent.cs       문서[1] 4.1 이벤트 계약
  Match/                   60Hz 매치 시뮬레이터
    FighterRuntime.cs        선수 런타임 상태 (문서[3] 3장)
    MatchSim.cs              전략층(1s)/전술층(0.2s·지터)/실행FSM(60Hz)
    Vec2.cs / ReplayFrame.cs  2D 위치(B) / 15Hz 뷰어 트랙
  Serialization/           MatchRecord ↔ JSON (schemaVer, 결정론)
Morituri.Headless/       배치 러너 + 분석 도구 + viewer.html 서버
Morituri.Sim.Tests/      테스트 62개 (자체 러너)
```

## 현재 위치 (M4-b)
- **공간 모델:** 1D → **2D-lite(B)** 전환 완료. 원형 핏(반경 12m), 유클리드 거리, 선회=각도이동, 충돌 disc.
  (1D 시절 "동속+직선=카이팅 불가" 진단 → B로 구조 전환, 잔존 동속점착은 인내심 메커니즘으로 해소. → 문서[8][9])
- **선구현 메커니즘:** 인내심·관중게이지·출혈(도끼)·패링(방패)·선취점쉴드·하이퍼아머·카이팅비용·
  스티어링/스페이싱 히스테리시스·판단주기 지터·천부(StatGen)·특성 14종(TraitGen).

## M2 디버깅으로 확정된 설계 결정 (배치 데이터 근거)
1. 동시 히트 해결 페이즈 — 순차 적용 시 선공 100:0 (거울전으로 검출)
2. 교전 거리 = min(선호거리, 사거리×교전비) — 아니면 영원한 대치 (거울전 KO 0%)
3. Utility 경로 선딜 캔슬 금지 — 캔슬은 성격 Interrupt 전용 (스윙 커밋 원칙)
4. 확정 기회(상대 캔슬불가 상태)엔 풀 사거리 발사 — 후딜 처벌 성립 조건
5. 자기 약점 거리(inner) 공격 가치 ×0.45 — 창의 인파이팅 자살 방지
6. 가드 점수 × (1−상대무기 GuardCrush) — 도끼 상대 가드 자살 방지
7. AttackGateScale 1.6→0.9 — 카운터형이 영원히 공격 불가했음

## 밸런스 작업 이력 (요약 — 상세는 문서[0][8][9])
- **M3-A:** 동일무기 전술 RPS 구조적 불가 판명 → "전술=무기 결합" 프레임 전환. 무기×빌드 매트릭스로 검증 전환.
- **M3-A2:** 무기 밸런스 + 하이퍼아머. 자동 데미지 스윕(검42→33·방패→방패교체·쌍검26×2→18×2 등).
- **M3-B:** 오만함 도발 역전 재설계 — 트리거를 `OppStaggeredWhileAhead`로 이동(역전패 0%→~8.5%).
- **M3-C:** 거울전 선공편향 — 1D 수용 후 2D화로 ~50/50 해소. 회귀테스트 [0.42,0.58].
- **M4-b:** disc 점착·5레버 폐기(bistability) → **인내심**으로 영원대치 해소·부분수렴. (문서[9] 종결)
