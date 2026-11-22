using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IAM.Persistence.SqlServer.Migrations.Request
{
    /// <inheritdoc />
    public partial class AccountNameOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DL-101: AccountNameSequence numbered existing names in no set order. The upgrade stops if any item's names are numbered
            // differently from when they were written, so "latest" never silently changes. An identity here already refuses a
            // value written by a login (no IDENTITY_INSERT), as PostgreSQL's GENERATED ALWAYS now does.
            migrationBuilder.Sql(@"IF EXISTS (SELECT 1 FROM [request].[AccountNameAssignment] a
           JOIN [request].[AccountNameAssignment] b ON a.[ItemId] = b.[ItemId] AND a.[Seq] < b.[Seq] AND a.[SetAtUtc] > b.[SetAtUtc])
    THROW 51001, N'an item''s account names are numbered out of the order they were written; resolve them before upgrading (DL-101)', 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the guard changed nothing.
        }
    }
}
