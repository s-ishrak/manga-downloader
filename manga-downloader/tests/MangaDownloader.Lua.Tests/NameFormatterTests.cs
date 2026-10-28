using System.Collections.Generic;
using MangaDownloader.Core.Naming;
using Xunit;

namespace MangaDownloader.Lua.Tests
{
    /// <summary>One test per formatting-string feature documented on the HDoujin wiki.</summary>
    public class NameFormatterTests
    {
        private static readonly Dictionary<string, string> Values = new Dictionary<string, string>
        {
            ["TITLE"] = "My Doujin",
            ["ARTIST"] = "Alice, Bob, Carol",
            ["CIRCLE"] = "",
            ["AUTHOR"] = "",
            ["PAGE"] = "7",
            ["CHAPTER#"] = "12",
            ["LANGUAGE"] = "English",
        };

        private static string F(string format) => NameFormatter.Format(format, Values);

        [Fact]
        public void PlainTokensAreCaseInsensitive()
        {
            Assert.Equal("My Doujin_7", F("%TITLE%_%PAGE%"));
            Assert.Equal("My Doujin_7", F("%title%_%Page%"));
            Assert.Equal("12 - My Doujin", F("%CHAPTER#% - %TITLE%"));
        }

        [Fact]
        public void FallbackUsesFirstNonEmptyAlternative()
        {
            Assert.Equal("Alice, Bob, Carol", F("%CIRCLE||ARTIST%"));
            Assert.Equal("Alice, Bob, Carol", F("%CIRCLE||AUTHOR||ARTIST%"));
            Assert.Equal("Unknown", F("%CIRCLE||AUTHOR||Unknown%")); // unknown word = literal
        }

        [Fact]
        public void ListTokensCanBeLimited()
        {
            Assert.Equal("Alice", F("%ARTIST(1)%"));
            Assert.Equal("Alice, Bob", F("%ARTIST(2)%"));
            Assert.Equal("Alice, Bob, Carol", F("%ARTIST(10)%"));
        }

        [Fact]
        public void NumbersCanBeZeroPadded()
        {
            Assert.Equal("007", F("%PAGE(000)%"));
            Assert.Equal("0007", F("%PAGE(0000)%"));
            Assert.Equal("12", F("%CHAPTER#(0)%"));
        }

        [Fact]
        public void IfSupportsEqualityInequalityAndTruthiness()
        {
            Assert.Equal("EN", F("%IF(%LANGUAGE%=English,EN,other)%"));
            Assert.Equal("other", F("%IF(%LANGUAGE%=French,FR,other)%"));
            Assert.Equal("not-fr", F("%IF(%LANGUAGE%!=French,not-fr,fr)%"));
            Assert.Equal("has artist", F("%IF(%ARTIST%,has artist,none)%"));
            Assert.Equal("none", F("%IF(%CIRCLE%,has circle,none)%"));
        }

        [Fact]
        public void IfCanBeNested()
        {
            Assert.Equal("B", F("%IF(%LANGUAGE%=French,A,%IF(%LANGUAGE%=English,B,C)%)%"));
        }

        [Fact]
        public void RegexAndReplace()
        {
            Assert.Equal("Doujin", F("%REGEX(%TITLE%,My (\\w+),1)%"));
            Assert.Equal("My_Doujin", F("%REPLACE(%TITLE%, ,_)%"));
            Assert.Equal("MY DOUJIN", F("%UPPER(%TITLE%)%"));
        }

        [Fact]
        public void TokensInsideFunctionsKeepTheirOwnArguments()
        {
            Assert.Equal("Alice, Bob!", F("%IF(%ARTIST%,%ARTIST(2)%!,none)%"));
        }

        [Fact]
        public void StrayPercentSignsAndUnknownTokensStayLiteral()
        {
            Assert.Equal("100% sure", F("100% sure"));
            Assert.Equal("%NOPE% x", F("%NOPE% x"));
            Assert.Equal("a % b % c", F("a % b % c"));
        }

        [Fact]
        public void PathNamesRemoveInvalidCharactersAndReservedNames()
        {
            Assert.Equal("a b c", PathNames.Sanitize("a: b? c*"));
            Assert.Equal("a_ b_ c_", PathNames.Sanitize("a: b? c*", '_'));
            Assert.Equal("_CON", PathNames.Sanitize("CON"));
            Assert.Equal("name", PathNames.Sanitize("name. . "));
            Assert.Equal("_", PathNames.Sanitize("???"));
        }
    }
}
