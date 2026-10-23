namespace CommandDeck;

/// <summary>
/// Keys of <see cref="CommandAction.Arguments"/> that more than one project, or
/// the launcher, reads. A feature's own keys stay with that feature.
/// </summary>
public static class ActionArgumentNames
{
    public const string Url = "url";
    public const string Dispatch = "dispatch";
    public const string Text = "text";
    public const string Path = "path";
    public const string UrlTemplate = "urlTemplate";
    public const string Alias = "alias";
    public const string Line = "line";
    public const string Column = "column";

    public const string Key = "key";

    // The shell command a trusted Omarchy menu entry runs.
    public const string Command = "command";
    public const string Argv = "argv";
    public const string WorkingDirectory = "workingDirectory";
    public const string ProviderId = "providerId";
    public const string ModelId = "modelId";
    public const string Prompt = "prompt";
    public const string BackendId = "backendId";
    public const string WorkflowId = "workflowId";
    public const string AttachmentKind = "attachmentKind";
    public const string ResultId = "resultId";
    public const string FilterId = "filterId";
    public const string Query = "query";
    public const string Page = "page";
    public const string Entry = "entry";
    public const string Template = "template";
    public const string Confirm = "confirm";
    public const string TopicId = "topicId";
    public const string AppUrl = "appUrl";
    public const string Change = "change";
    public const string PostingId = "postingId";
    public const string BoxId = "boxId";
    public const string TargetId = "targetId";
    public const string TargetKind = "targetKind";
    public const string InputPrefix = "input:";
    public const string Terminal = "terminal";
}
