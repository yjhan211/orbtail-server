// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using network.common.data.helpers;

namespace network.common.data
{
    public class LocalizedText
    {
        public LocalizedText(string kr, string en = "")
        {
            Kr = kr ?? "";
            En = en ?? "";
        }

        public string Kr { get; }
        public string En { get; }

        /// <summary>
        /// 지정 언어의 텍스트 반환. CSV에 _en 컬럼 추가 시 자동 지원.
        /// </summary>
        public string GetText(string lang = "kr")
        {
            return lang switch
            {
                "kr" => Kr,
                "en" => En,
                _ => Kr
            };
        }

        /// <summary>
        /// CsvRow에서 {prefix}_kr, {prefix}_en 컬럼으로 LocalizedText 생성.
        /// </summary>
        public static LocalizedText FromCsv(CsvRow row, string prefix)
        {
            var kr = row[$"{prefix}_kr"];
            var en = row.ContainsKey($"{prefix}_en") ? row[$"{prefix}_en"] : "";
            return new LocalizedText(kr, en);
        }

        public override string ToString()
        {
            return Kr;
        }
    }
}
