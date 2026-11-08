using TeamSpeakManager.Core.Services.ServerQuery;

namespace LTS.Tests;

public class ServerQueryParserTests
{
    [Fact]
    public void Parses_error_line_with_ok()
    {
        var reply = ServerQueryParser.ParseSingle("error id=0 msg=ok");
        Assert.True(reply.IsOk);
        Assert.Equal(0, reply.ErrorId);
        Assert.Equal("ok", reply.ErrorMessage);
        Assert.Empty(reply.Records);
    }

    [Fact]
    public void Parses_error_line_with_non_zero_id()
    {
        var reply = ServerQueryParser.ParseSingle("error id=2568 msg=insufficient\\ssupports\\spriority");
        Assert.False(reply.IsOk);
        Assert.Equal(2568, reply.ErrorId);
        Assert.Equal("insufficient supports priority", reply.ErrorMessage);
    }

    [Fact]
    public void Parses_single_record()
    {
        var reply = ServerQueryParser.ParseSingle("virtualserver_maxclients=32 virtualserver_uptime=86400\nerror id=0 msg=ok");
        Assert.True(reply.IsOk);
        Assert.Single(reply.Records);
        Assert.Equal("32", reply.Records[0]["virtualserver_maxclients"]);
        Assert.Equal("86400", reply.Records[0]["virtualserver_uptime"]);
    }

    [Fact]
    public void Parses_multiple_records_with_pipe_separation()
    {
        var raw = "clid=1 client_nickname=Alice\nclid=2 client_nickname=Bob\nerror id=0 msg=ok";
        var reply = ServerQueryParser.ParseSingle(raw);
        Assert.True(reply.IsOk);
        Assert.Equal(2, reply.Records.Count);
        Assert.Equal("Alice", reply.Records[0]["client_nickname"]);
        Assert.Equal("Bob", reply.Records[1]["client_nickname"]);
    }

    [Fact]
    public void Unescapes_pipe_in_nickname()
    {
        // nickname "A|B" is sent as A\pB and must not split into two records
        var raw = "clid=1 client_nickname=A\\pB\nerror id=0 msg=ok";
        var reply = ServerQueryParser.ParseSingle(raw);
        Assert.True(reply.IsOk);
        Assert.Single(reply.Records);
        Assert.Equal("A|B", reply.Records[0]["client_nickname"]);
    }

    [Fact]
    public void Unescapes_space_and_backslash()
    {
        Assert.Equal("hello world", ServerQueryParser.Unescape("hello\\sworld"));
        Assert.Equal("a\\b", ServerQueryParser.Unescape("a\\\\b"));
    }

    [Fact]
    public void Roundtrip_escape_unescape_is_stable()
    {
        foreach (var s in new[] { "plain", "with space", "pipe|char", "back\\slash", "new\nline" })
        {
            Assert.Equal(s, ServerQueryParser.Unescape(ServerQueryParser.Escape(s)));
        }
    }

    [Fact]
    public void Escape_neutralises_argument_injection()
    {
        // Spaces and newlines are what let a password split into extra parameters or a
        // second command. Escaping must remove every literal one from the output.
        var escaped = ServerQueryParser.Escape("pass word=evil\nsecond=1");
        Assert.DoesNotContain(' ', escaped);
        Assert.DoesNotContain('\n', escaped);
        // No bare "client_login_name=..." style parameter can be formed from the value.
        Assert.Equal(1, escaped.Split(' ').Length);
        // A password containing '=' stays a single value: ServerQuery splits on the first '=' only.
        Assert.Equal("a=b", ServerQueryParser.Unescape(ServerQueryParser.Escape("a=b")));
    }

    [Fact]
    public void Incomplete_response_reports_error_not_success()
    {
        var reply = ServerQueryParser.ParseSingle("virtualserver_maxclients=32");
        Assert.False(reply.IsOk);
        Assert.Equal(-1, reply.ErrorId);
    }

    [Fact]
    public void Empty_response_is_error()
    {
        var reply = ServerQueryParser.ParseSingle("");
        Assert.False(reply.IsOk);
        Assert.Empty(reply.Records);
    }

    [Fact]
    public void Ignores_carriage_returns()
    {
        var reply = ServerQueryParser.ParseSingle("clid=1\r\nclid=2\r\nerror id=0 msg=ok\r\n");
        Assert.True(reply.IsOk);
        Assert.Equal(2, reply.Records.Count);
    }

    [Fact]
    public void Multiple_replies_are_all_parsed()
    {
        var raw = "client_id=1\nerror id=0 msg=ok\nclient_id=2\nerror id=0 msg=ok";
        var replies = ServerQueryParser.ParseMany(raw);
        Assert.Equal(2, replies.Count);
    }

    [Fact]
    public void Values_with_equals_are_preserved()
    {
        var reply = ServerQueryParser.ParseSingle("client_description=a=b=c\nerror id=0 msg=ok");
        Assert.Equal("a=b=c", reply.Records[0]["client_description"]);
    }
}
