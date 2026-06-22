namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Attributes
{
    public sealed class PercentValueAttribute : Attribute
    {
        public short PercentValue { get; }

        public PercentValueAttribute(short value)
        {
            PercentValue = value;
        }
    }
}
