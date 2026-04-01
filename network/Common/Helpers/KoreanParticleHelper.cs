// ReSharper disable All

namespace Common.Helpers
{
    /// <summary>
    ///     한국어 조사 처리 헬퍼
    /// </summary>
    public static class KoreanParticleHelper
    {
        /// <summary>
        ///     마지막 글자의 받침 유무 확인
        /// </summary>
        public static bool HasFinalConsonant(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var lastChar = text[text.Length - 1];

            // 한글 범위 확인 (가-힣: 0xAC00-0xD7A3)
            if (lastChar < 0xAC00 || lastChar > 0xD7A3)
            {
                // 한글이 아닌 경우 - 숫자나 영문
                // 숫자: 0,1,3,6,7,8 → 받침 있음 / 2,4,5,9 → 받침 없음
                return lastChar switch
                {
                    '0' => true, // 영
                    '1' => true, // 일
                    '3' => true, // 삼
                    '6' => true, // 육
                    '7' => true, // 칠
                    '8' => true, // 팔
                    '2' => false, // 이
                    '4' => false, // 사
                    '5' => false, // 오
                    '9' => false, // 구
                    _ => false // 기타 (영문 등)
                };
            }

            // 한글인 경우: (lastChar - 0xAC00) % 28 이 0이면 받침 없음
            return (lastChar - 0xAC00) % 28 != 0;
        }

        /// <summary>
        ///     을/를 선택
        /// </summary>
        public static string GetEulReul(string text)
        {
            return HasFinalConsonant(text) ? "을" : "를";
        }

        /// <summary>
        ///     이/가 선택
        /// </summary>
        public static string GetIGa(string text)
        {
            return HasFinalConsonant(text) ? "이" : "가";
        }

        /// <summary>
        ///     은/는 선택
        /// </summary>
        public static string GetEunNeun(string text)
        {
            return HasFinalConsonant(text) ? "은" : "는";
        }

        /// <summary>
        ///     과/와 선택
        /// </summary>
        public static string GetGwaWa(string text)
        {
            return HasFinalConsonant(text) ? "과" : "와";
        }

        /// <summary>
        ///     아/야 선택
        /// </summary>
        public static string GetAYa(string text)
        {
            return HasFinalConsonant(text) ? "아" : "야";
        }

        /// <summary>
        ///     이에요/예요 선택
        /// </summary>
        public static string GetIeyoYeyo(string text)
        {
            return HasFinalConsonant(text) ? "이에요" : "예요";
        }

        /// <summary>
        ///     으로/로 선택
        /// </summary>
        public static string GetEuroRo(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "로";
            }

            var lastChar = text[text.Length - 1];

            // 한글 범위 확인
            if (lastChar < 0xAC00 || lastChar > 0xD7A3)
            {
                return "로";
            }

            var finalConsonant = (lastChar - 0xAC00) % 28;
            // 받침 없거나 ㄹ 받침(8)이면 "로", 그 외 "으로"
            return finalConsonant == 0 || finalConsonant == 8 ? "로" : "으로";
        }

        /// <summary>
        ///     텍스트 내의 조사 플레이스홀더 치환
        ///     예: "{을/를}" → "을" 또는 "를" (앞 단어 기준)
        /// </summary>
        public static string ReplaceParticles(string text, string precedingWord)
        {
            text = text.Replace("{을/를}", GetEulReul(precedingWord));
            text = text.Replace("{이/가}", GetIGa(precedingWord));
            text = text.Replace("{은/는}", GetEunNeun(precedingWord));
            text = text.Replace("{과/와}", GetGwaWa(precedingWord));
            text = text.Replace("{으로/로}", GetEuroRo(precedingWord));
            return text;
        }
    }
}
