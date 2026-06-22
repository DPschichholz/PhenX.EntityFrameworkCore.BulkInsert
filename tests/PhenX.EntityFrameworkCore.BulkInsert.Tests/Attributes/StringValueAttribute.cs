namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Attributes
{
	public sealed class StringValueAttribute : Attribute
	{
		public string StringValue { get; }

		public StringValueAttribute(string value)
		{
			StringValue = value;
		}
	}
}