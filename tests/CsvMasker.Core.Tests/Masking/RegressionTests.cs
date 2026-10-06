using System.Security.Cryptography;
using System.Text;
using CsvMasker.Core.Masking;
using static CsvMasker.Core.Tests.TestFiles;

namespace CsvMasker.Core.Tests.Masking;

/// <summary>
/// Pins the exact output of a plan with no entity groups, so changes to seeding (step 7) can't
/// silently change how existing plans mask. If this fails after an intentional change, update
/// the hash and say so in the commit.
/// </summary>
public class RegressionTests
{
    // SHA-256 of the masked output, captured before entity groups were added.
    private const string ExpectedHash = "46a4c554123320ae5daf65bf652e668879cdb45bd1db7e16bbf673ba0b16b351";

    internal static byte[] MaskReferenceFile()
    {
        var csv = new StringBuilder("Id,Name,First,Email,Phone,Zip,Amount,Qty,Date,Notes,Text,Company,Street,City,Hospital,Code\r\n");
        for (int i = 0; i < 300; i++)
        {
            csv.Append($"ID-{i % 120:D5},Person {i % 90},First{i % 70},user{i % 80}@corp.com,(555) {200 + i % 700}-{1000 + i},{(1000 + i * 37) % 100000:D5},");
            csv.Append($"{(i % 7 == 0 ? "N/A" : $"{i * 12.5:F2}")},{i % 9},2024-{i % 12 + 1:D2}-{i % 28 + 1:D2},note {i},\"Free text, row {i}\",");
            csv.Append($"Company {i % 50},{i} Main St,City {i % 30},Hospital {i % 20},{(i % 3 == 0 ? "" : $"C{i % 60:D3}")}\r\n");
        }

        var plan = new MaskingPlan(new Dictionary<string, ColumnRule>
        {
            ["Id"] = new(MaskingStrategy.HashId, new HashIdOptions("T-")),
            ["Name"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.PersonFull)),
            ["First"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.PersonFirst)),
            ["Email"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Email)),
            ["Phone"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Phone)),
            ["Zip"] = new(MaskingStrategy.ZipRemap),
            ["Amount"] = new(MaskingStrategy.Perturb),
            ["Qty"] = new(MaskingStrategy.Perturb, new PerturbOptions(Mode: PerturbMode.Global, IsCount: true)),
            ["Date"] = new(MaskingStrategy.DateShift, new DateShiftOptions(KeepWeekday: true)),
            ["Notes"] = new(MaskingStrategy.Redact),
            ["Text"] = new(MaskingStrategy.Lorem),
            ["Company"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Company)),
            ["Street"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.StreetAddress)),
            ["City"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.City), MappingDomain: "place"),
            ["Hospital"] = new(MaskingStrategy.Fake, new FakeOptions(FakeKind.Hospital)),
            ["Code"] = new(MaskingStrategy.HashId),
        });

        using var session = new MaskingSession((byte[])TestMasker.KeyA.Clone(), null);
        using var output = new MemoryStream();
        session.Run(new MemoryStream(Encode(csv.ToString(), Utf8)), output, plan);
        return output.ToArray();
    }

    [Fact]
    public void Plans_without_entity_groups_mask_exactly_as_before()
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(MaskReferenceFile()));

        Assert.True(ExpectedHash == hash, $"Masked output changed. Hash: [{hash}]");
    }
}
