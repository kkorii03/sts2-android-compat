using System;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.Patches;

/// <summary>
/// Korean text for UI the compat layer adds. Chinese/English paths are left to the
/// callers' existing T(zh, en) helpers; this only swaps English for Korean when the
/// game language is Korean ("kor", see LocManager._weblateToGameLanguage).
/// </summary>
internal static class ModText
{
    private static readonly Type LocManagerType = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Localization.LocManager");
    private static readonly PropertyInfo InstanceProperty = LocManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
    private static readonly PropertyInfo LanguageProperty = LocManagerType?.GetProperty("Language", BindingFlags.Public | BindingFlags.Instance);

    internal static bool IsKo()
    {
        try
        {
            var instance = InstanceProperty?.GetValue(null);
            var language = instance == null ? string.Empty : LanguageProperty?.GetValue(instance) as string ?? string.Empty;
            return language.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>English text, or its Korean entry from the table below when the game is in Korean.</summary>
    internal static string Ko(string en) => en != null && IsKo() && Korean.TryGetValue(en, out var ko) ? ko : en;

    /// <summary>For formatted text that cannot be a table key.</summary>
    internal static string Ko(string en, string ko) => IsKo() ? ko : en;

    private static readonly Dictionary<string, string> Korean = new(StringComparer.Ordinal)
    {
        // In-game "Mobile" settings tab (AndroidInGameSettingsPatches)
        ["Mobile"] = "모바일",
        ["Mobile Port Settings"] = "모바일 설정",
        ["Android companion settings"] = "추가 설정",
        ["Open"] = "열기",
        ["Display / Graphics"] = "화면 / 그래픽",
        ["Shader compatibility"] = "셰이더 호환 모드",
        ["Rotation mode"] = "화면 회전",
        ["Render resolution"] = "렌더링 해상도",
        ["Font size"] = "글꼴 크기",
        ["Game scale"] = "게임 크기",
        ["Touch / Hand"] = "터치 / 손패",
        ["Show more hand-card text"] = "손패 카드 글자 더 보기",
        ["Hand-card lift height"] = "손패 카드 들어 올림 높이",
        ["Hand card size"] = "손패 카드 크기",
        ["End turn / energy size"] = "턴 종료 / 에너지 크기",
        ["Touch-lift preview"] = "탭하면 카드 크게 보기",
        ["Tooltip display"] = "툴팁 표시",
        ["Immediate"] = "바로 표시",
        ["Long press 1s"] = "1초 길게 누르기",
        ["Hidden"] = "숨김",
        ["Touch-lift re-tap action"] = "들어 올린 카드 다시 탭",
        ["Put down card"] = "카드 내려놓기",
        ["Play card"] = "카드 사용",
        ["None"] = "없음",
        ["Slide to browse hand"] = "밀어서 손패 훑어보기",
        ["Accidental-play guard"] = "실수로 카드 사용 방지",
        ["Enemy tap assist"] = "적 탭 보조",
        ["Mobile selection confirmation"] = "선택 후 확인 버튼",
        ["Confirm button near selection"] = "확인 버튼을 선택한 곳 근처에",
        ["Two-finger inspect"] = "두 손가락으로 자세히 보기",
        ["Drag-scroll lists (drag never selects)"] = "목록 끌어서 스크롤 (끌 때는 선택 안 됨)",
        ["System / Compatibility"] = "시스템 / 호환성",
        ["Enable preload"] = "미리 불러오기 사용",
        ["Display refresh rate"] = "화면 주사율",
        ["High refresh rate (default)"] = "높은 주사율 (기본)",
        ["Request 60 Hz"] = "60Hz 요청",
        ["Follow system"] = "시스템 설정 따름",
        ["Startup common resources"] = "시작 시 공통 리소스",
        ["Main menu resources"] = "메인 메뉴 리소스",
        ["Menu hotspot warmup"] = "자주 쓰는 메뉴 미리 준비",
        ["VFX warmup"] = "시각 효과 미리 준비",
        ["Off"] = "끔",
        ["High priority"] = "자주 쓰는 것만",
        ["All"] = "전체",
        ["Combat code warmup"] = "전투 코드 미리 준비",
        ["Shader warmup"] = "셰이더 미리 준비",
        ["Load resources"] = "리소스 불러오기",
        ["Run / room preloading"] = "진행 중 방 미리 불러오기",
        ["Audio compatibility mode"] = "소리 호환 모드",
        ["Volume up opens keyboard"] = "볼륨 올림 키로 키보드 열기",
        ["Show multiplayer emoji button"] = "멀티플레이 감정 표현 버튼 표시",
        ["Enable quick retry button"] = "빠른 다시 하기 버튼 사용",
        ["Enable experimental custom max players"] = "최대 인원 직접 설정 사용 (실험적)",
        ["Experimental max players"] = "최대 인원 (실험적)",
        ["Local multiplayer patch"] = "근거리(LAN) 멀티플레이 패치",
        ["LAN custom player ID"] = "LAN 플레이어 ID 직접 지정",
        ["Auto"] = "자동",
        ["No rotation"] = "회전 안 함",
        ["180°"] = "180° 회전",
        ["Portrait (requires portrait UI MOD)"] = "세로 화면 (세로 UI 모드 필요)",

        // Fallback settings row (ExternalSettingsPatches)
        ["OPEN"] = "열기",

        // Android startup / preload loading screen
        ["Optimizing startup..."] = "시작 최적화 중...",
        ["Preparing resource warmup"] = "리소스 미리 준비 중",
        ["Optimizing combat code..."] = "전투 코드 최적화 중...",
        ["Preparing first attack hot paths"] = "첫 공격 준비 중",
        ["Startup optimization complete"] = "시작 최적화 완료",
        ["Ready"] = "준비 완료",
        ["Launching main menu"] = "메인 메뉴 여는 중",
        ["Loading main menu..."] = "메인 메뉴 불러오는 중...",
        ["Creating main menu scene"] = "메인 메뉴 화면 만드는 중",
        ["Startup complete"] = "시작 완료",
        ["Preload disabled"] = "미리 불러오기 꺼짐",
        ["Preloading gameplay resources..."] = "게임 리소스 미리 불러오는 중...",
        ["First startup warmup may take a bit longer on Android"] = "첫 실행은 준비 때문에 조금 더 오래 걸릴 수 있습니다",
        ["Rendering combat effects..."] = "전투 효과 그리는 중...",
        ["Compiling combat effects..."] = "전투 효과 준비 중...",
        ["Preparing combat warmup..."] = "전투 미리 준비 중...",
        ["Preparing scene warmup"] = "화면 미리 준비 중",
        ["Loading resources..."] = "리소스 불러오는 중...",
        ["Bulk asset preload skipped"] = "일괄 미리 불러오기 건너뜀",
    };
}
