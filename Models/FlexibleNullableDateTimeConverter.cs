using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sync104ToBpmErp.Models
{
    /// <summary>
    /// 104 API 日期欄位格式不一致（常見 yyyy/MM/dd，亦可能為空字串或因權限被遮蔽），
    /// System.Text.Json 預設的 DateTime 轉換器僅接受嚴格 ISO-8601 格式，遇到不符格式會直接
    /// 拋例外並讓整批資料反序列化失敗。改用寬鬆解析：解析失敗或空值一律視為 null，不中斷同步。
    /// </summary>
    public class FlexibleNullableDateTimeConverter : JsonConverter<DateTime?>
    {
        private static readonly string[] Formats =
        {
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd",
        };

        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType != JsonTokenType.String)
                return null;

            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (DateTime.TryParseExact(value, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                return exact;

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
                return loose;

            return null;
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd HH:mm:ss"));
            else
                writer.WriteNullValue();
        }
    }
}
