.pragma library
.import "../Protocol.js" as Protocol

function mode(preferences) {
  const value = preferences ? preferences.workspacePreviewMode : Protocol.WorkspacePreviewModes.Layout
  return value === Protocol.WorkspacePreviewModes.Off || value === Protocol.WorkspacePreviewModes.Snapshots ? value : Protocol.WorkspacePreviewModes.Layout
}

function displayMode(acknowledged, draft) {
  const accepted = mode(acknowledged)
  const requested = mode(draft)
  if (accepted === Protocol.WorkspacePreviewModes.Off || requested === Protocol.WorkspacePreviewModes.Off) return Protocol.WorkspacePreviewModes.Off
  return accepted === Protocol.WorkspacePreviewModes.Snapshots && requested === Protocol.WorkspacePreviewModes.Snapshots ? Protocol.WorkspacePreviewModes.Snapshots : Protocol.WorkspacePreviewModes.Layout
}

function sourceEnabled(preferences, home) {
  if (!preferences || !preferences.sources) return false
  const source = preferences.sources.find(value => value.sourceId === Protocol.SourceIds.WindowsWorkspaces)
  return !!source && (home ? source.enabledOnHome === true : source.enabledInSearch === true)
}

function identity(result) {
  if (!result || result.providerId !== Protocol.ProviderIds.Hyprland || result.sourceId !== Protocol.SourceIds.WindowsWorkspaces
      || result.kind !== Protocol.ResultKinds.Workspace || !result.metadata) return null
  const data = result.metadata
  if (data.workspaceKind !== Protocol.WorkspaceIdentityKinds.Ordinary
      && data.workspaceKind !== Protocol.WorkspaceIdentityKinds.Special) return null
  const id = Number(data.workspaceId)
  if (!Number.isInteger(id) || id < -2147483648 || id > 2147483647
      || id === 0 || id === -1 || String(id) !== data.workspaceId) return null
  if (typeof data.workspaceName !== "string" || !data.workspaceName.length
      || typeof data.monitorName !== "string" || !data.monitorName.length) return null
  return { id: id, name: data.workspaceName, monitor: data.monitorName, kind: data.workspaceKind }
}

function eligible(result, acknowledged, draft, home) {
  return identity(result) !== null && acknowledged !== null
    && mode(acknowledged) !== Protocol.WorkspacePreviewModes.Off && mode(draft) !== Protocol.WorkspacePreviewModes.Off
    && sourceEnabled(acknowledged, home) && sourceEnabled(draft || acknowledged, home)
}

function eligibleForQuery(result, acknowledged, draft, queryText, filters) {
  const home = queryText.trim().length === 0 && filters.length === 0
  return eligible(result, acknowledged, draft, home)
}

function filtersAllowWorkspace(filters, descriptors) {
  let hasPositive = false
  let positiveMatch = false
  for (const filter of filters || []) {
    const descriptor = (descriptors || []).find(value => value.id === filter.id)
    const matches = !!descriptor
      && (!descriptor.providerId || descriptor.providerId === Protocol.ProviderIds.Hyprland)
      && (!descriptor.sourceId || descriptor.sourceId === Protocol.SourceIds.WindowsWorkspaces)
      && (!descriptor.kind || descriptor.kind === Protocol.ResultKinds.Workspace)
    if (filter.excluded && matches) return false
    if (!filter.excluded) {
      hasPositive = true
      positiveMatch = positiveMatch || matches
    }
  }
  return !hasPositive || positiveMatch
}
