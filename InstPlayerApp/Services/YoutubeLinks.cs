namespace InstPlayerApp.Services;

/// <summary>공유/클립보드 텍스트("영상 제목 https://youtu.be/XXXX")에서 유튜브 주소를 뽑는다.</summary>
public static class YoutubeLinks
{
    // youtube.com/watch 는 www./m./music. 하위 도메인을 모두 포함한다
    private static readonly string[] Markers =
    {
        "youtube.com/watch", "youtu.be/", "youtube.com/shorts/", "youtube.com/live/",
    };

    public static string? Extract(string text)
    {
        foreach (var part in text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var m in Markers)
                if (part.Contains(m, StringComparison.OrdinalIgnoreCase))
                    return part;
        }
        return null;
    }
}
