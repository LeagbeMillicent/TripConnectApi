using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiconRide.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TransportOperatorIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "transport_operators",
                type: "boolean",
                nullable: false,
                defaultValue: true); // existing operators stay schedulable
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_active",
                table: "transport_operators");
        }
    }
}
