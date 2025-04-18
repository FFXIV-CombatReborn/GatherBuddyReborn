﻿// <auto-generated />
using System;
using GatherBuddy.Web.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace GatherBuddy.Web.Database.Migrations
{
    [DbContext(typeof(GatherBuddyDbContext))]
    [Migration("20250417023902_InitialMigration")]
    partial class InitialMigration
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "9.0.3")
                .HasAnnotation("Relational:MaxIdentifierLength", 64);

            modelBuilder.Entity("GatherBuddy.Models.SimpleFishRecord", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("char(36)");

                    b.Property<byte>("Amount")
                        .HasColumnType("tinyint unsigned");

                    b.Property<uint>("BaitId")
                        .HasColumnType("int unsigned");

                    b.Property<ushort>("Bite")
                        .HasColumnType("smallint unsigned");

                    b.Property<uint>("CatchId")
                        .HasColumnType("int unsigned");

                    b.Property<uint>("Effects")
                        .HasColumnType("int unsigned");

                    b.Property<ushort>("FishingSpot")
                        .HasColumnType("smallint unsigned");

                    b.Property<ushort>("Gathering")
                        .HasColumnType("smallint unsigned");

                    b.Property<ushort>("Perception")
                        .HasColumnType("smallint unsigned");

                    b.Property<float>("PositionX")
                        .HasColumnType("float");

                    b.Property<float>("PositionY")
                        .HasColumnType("float");

                    b.Property<float>("PositionZ")
                        .HasColumnType("float");

                    b.Property<float>("Rotation")
                        .HasColumnType("float");

                    b.Property<int>("Timestamp")
                        .HasColumnType("int");

                    b.Property<byte>("TugAndHook")
                        .HasColumnType("tinyint unsigned");

                    b.HasKey("Id");

                    b.ToTable("FishRecords");
                });
#pragma warning restore 612, 618
        }
    }
}
