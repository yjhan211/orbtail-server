using network.common.data.helpers;

namespace server_tests;

public class CsvHelperTests
{
    [Fact]
    public void LoadCsv_Pads_Missing_Trailing_Optional_Columns()
    {
        string path = Path.Combine(Path.GetTempPath(), $"csv-helper-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "id,name,optional\n1,alpha\n2,beta,value\n");

        try
        {
            var rows = CsvHelper.LoadCsv(path);

            Assert.Equal("", rows[0]["optional"]);
            Assert.Equal("value", rows[1]["optional"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
