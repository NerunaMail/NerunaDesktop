using Neruna.Core.Mail;

namespace Neruna.Client.Tests;

public class StyleBlocksTests
{
    [Fact]
    public void Unclosed_media_block_is_closed()
    {
        var html = StyleBlocks.CloseOpenBraces("<style>p { color: red; } @media screen { .a { width: 10px; }</style><p>Hallo</p>");
        Assert.Equal("<style>p { color: red; } @media screen { .a { width: 10px; }}</style><p>Hallo</p>", html);
    }

    [Fact]
    public void Style_block_cut_off_without_end_tag_is_closed()
    {
        Assert.Equal("<style>@media all { .a { x: 1;}}", StyleBlocks.CloseOpenBraces("<style>@media all { .a { x: 1;"));
    }

    [Fact]
    public void Braces_in_comments_do_not_count_and_an_open_comment_is_ended_first()
    {
        Assert.Equal("<style>/* { */ p { }</style>", StyleBlocks.CloseOpenBraces("<style>/* { */ p { }</style>"));
        Assert.Equal("<style>@media x { p { } /* rest*/}</style>", StyleBlocks.CloseOpenBraces("<style>@media x { p { } /* rest</style>"));
    }

    [Fact]
    public void Stray_closing_brace_before_does_not_hide_an_open_block_after()
    {
        Assert.Equal("<style>} @media x { p {}}</style>", StyleBlocks.CloseOpenBraces("<style>} @media x { p {}</style>"));
    }

    [Fact]
    public void Balanced_styles_and_mails_without_styles_stay_unchanged()
    {
        const string balanced = "<style>@media screen { .a { width: 10px; } }</style><p>{ Text }</p>";
        Assert.Equal(balanced, StyleBlocks.CloseOpenBraces(balanced));
        Assert.Equal("<p>{</p>", StyleBlocks.CloseOpenBraces("<p>{</p>"));
    }
}
