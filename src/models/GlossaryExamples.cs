namespace LiveCaptionsTranslator.models;

public static class GlossaryExamples
{
    public static GlossaryEntry[] Streaming() =>
    [
        Term("bilibili | ビリビリ | B站", "哔哩哔哩", "指视频或直播平台时使用；触电、发麻、撕裂等拟声词ビリビリ按原意翻译。"),
        Term("艦長 | かんちょう | 舰长", "舰长", "B站直播付费支持等级；不译成大会员，不混同提督、总督。真实舰船、历史或游戏军职按原意。"),
        Term("提督 | ていとく", "提督", "B站直播付费支持等级，不混同舰长、总督；历史或游戏军职按原意。"),
        Term("総督 | 总督", "总督", "B站直播付费支持等级，不混同舰长、提督；历史或游戏军职按原意。"),
        Term("大航海", "大航海", "B站直播会员体系名称；其他语境按原意。"),
        Term("艦長になって | 舰长になって | 上舰", "上舰", "仅指开通B站舰长时使用，例如感谢开通可译为谢谢你上舰；真实军职任命按原意。"),
        Term("艦長を更新 | 艦長の更新 | 续舰", "续舰", "仅指续费B站舰长时使用。"),
        Term("SC | スーパーチャット", "SC", "直播付费留言，可按语境译醒目留言；普通聊天评论不是付费留言，其他领域的SC按原意。"),
        Term("配信", "直播", "观看主播直播的语境；软件分发、歌曲发行等按原意。"),
        Term("配信者 | ライバー", "主播", "网络直播内容创作者。"),
        Term("初配信", "首播", "主播首次直播。"),
        Term("雑談配信", "闲聊直播", "主播和观众聊天的直播。"),
        Term("コラボ配信", "联动直播", "多位主播共同直播。"),
        Term("切り抜き", "直播切片", "直播内容节选视频；剪纸等非直播语境按原意。"),
        Term("メンバー | メンバーシップ", "会员", "YouTube等平台的频道会员；不擅自改成B站舰长。普通团队成员按原意翻译。")
    ];

    private static GlossaryEntry Term(string source, string translation, string context) =>
        new() { Source = source, Translation = translation, Context = context };
}
