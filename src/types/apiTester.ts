export type HttpMethodType = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

export const HTTP_METHODS: HttpMethodType[] = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'];

export type BodyType = 'None' | 'Json' | 'Text' | 'FormUrlEncoded';

export const BODY_TYPES: { value: BodyType; label: string }[] = [
  { value: 'None', label: 'Aucun' },
  { value: 'Json', label: 'JSON' },
  { value: 'Text', label: 'Texte' },
  { value: 'FormUrlEncoded', label: 'Formulaire (x-www-form-urlencoded)' },
];

export type AuthType = 'None' | 'Bearer' | 'Basic' | 'ApiKey';

export const AUTH_TYPES: { value: AuthType; label: string }[] = [
  { value: 'None', label: 'Aucune' },
  { value: 'Bearer', label: 'Bearer Token' },
  { value: 'Basic', label: 'Basic Auth' },
  { value: 'ApiKey', label: 'API Key' },
];

export interface KeyValueItem {
  id: string;
  key: string;
  value: string;
  isEnabled: boolean;
}

export interface AuthConfig {
  type: AuthType;
  token: string;
  username: string;
  password: string;
  apiKeyName: string;
  apiKeyValue: string;
  apiKeyInHeader: boolean;
}

export interface RequestModel {
  method: HttpMethodType;
  url: string;
  queryParams: KeyValueItem[];
  headers: KeyValueItem[];
  bodyType: BodyType;
  body: string;
  formFields: KeyValueItem[];
  auth: AuthConfig;
  timeoutSeconds: number;
}

export interface ResponseModel {
  statusCode: number;
  reasonPhrase: string;
  isSuccess: boolean;
  durationMs: number;
  sizeBytes: number;
  contentType: string;
  headers: KeyValueItem[];
  body: string;
  errorMessage: string | null;
}

export type StatusCategory =
  | 'None'
  | 'Success'
  | 'Redirect'
  | 'ClientError'
  | 'ServerError'
  | 'NetworkError';

export type ResponseFilterMode = 'Auto' | 'JsonPath' | 'Regex' | 'XPath';

export const RESPONSE_FILTER_MODES: { value: ResponseFilterMode; label: string }[] = [
  { value: 'Auto', label: 'Auto' },
  { value: 'JsonPath', label: 'JSONPath' },
  { value: 'Regex', label: 'Regex' },
  { value: 'XPath', label: 'XPath' },
];

export interface VaryingParam {
  id: string;
  name: string;
  value: string;
  isEnabled: boolean;
  isVarying: boolean;
  start: number;
  end: number;
  step: number;
}

export type VariableMode = 'Range' | 'List' | 'Random';

export const VARIABLE_MODES: { value: VariableMode; label: string }[] = [
  { value: 'Range', label: 'Plage (incrément)' },
  { value: 'List', label: 'Liste de valeurs' },
  { value: 'Random', label: 'Aléatoire' },
];

export type NumberBase = 10 | 16 | 2 | 8;

export const NUMBER_BASES: { value: NumberBase; label: string }[] = [
  { value: 10, label: 'Décimal (10)' },
  { value: 16, label: 'Hexadécimal (16)' },
  { value: 2, label: 'Binaire (2)' },
  { value: 8, label: 'Octal (8)' },
];

export type RandomKind = 'Integer' | 'HexBytes' | 'Text' | 'Guid' | 'PickFromList';

export const RANDOM_KINDS: { value: RandomKind; label: string }[] = [
  { value: 'Integer', label: 'Entier aléatoire' },
  { value: 'HexBytes', label: 'Hexadécimal (octets)' },
  { value: 'Text', label: 'Texte aléatoire' },
  { value: 'Guid', label: 'UUID / GUID' },
  { value: 'PickFromList', label: 'Choix dans une liste' },
];

export interface BatchVariable {
  id: string;
  name: string;
  locations: string[];
  isUsed: boolean;
  isVarying: boolean;
  fixedValue: string;
  mode: VariableMode;
  base: NumberBase;
  startText: string;
  endText: string;
  stepText: string;
  width: number;
  uppercaseHex: boolean;
  usePrefix: boolean;
  listText: string;
  randomKind: RandomKind;
  randomCount: number;
  isUnique: boolean;
  seedText: string;
  autoSeed: number;
  minText: string;
  maxText: string;
  byteCount: number;
  textLength: number;
  includeLowercase: boolean;
  includeUppercase: boolean;
  includeDigits: boolean;
  includeDashUnderscore: boolean;
}

export type BatchAxisKind = 'UrlParam' | 'Variable' | 'ExtractedList';

export interface BatchAxis {
  id: string;
  kind: BatchAxisKind;
  name: string;
  displayName: string;
  valueCount: number;
  valuesPreview: string;
  getValue: (index: number) => string;
  sourceParamId?: string;
  sourceVariableId?: string;
  rangeSpec?: {
    start: string;
    end: string;
    step: string;
    base: NumberBase;
    width: number;
    uppercaseHex: boolean;
    usePrefix: boolean;
  };
  listValues?: string[];
}

export type BatchMode = 'Combinations' | 'Parallel';

export const BATCH_MODES: { value: BatchMode; label: string }[] = [
  { value: 'Combinations', label: 'Combinaisons' },
  { value: 'Parallel', label: 'En parallèle' },
];

export interface RateLimitPolicy {
  isEnabled: boolean;
  maxRetriesPerRequest: number;
  backoffInitialMs: number;
  backoffMaxMs: number;
  backoffMultiplier: number;
  respectRetryAfter: boolean;
  maxRetryAfterSeconds: number;
  extraRateLimitStatusCodes: string;
  pauseFloorMs: number;
  quotaRemainingHeader: string;
  quotaResetHeader: string;
  quotaThreshold: number;
}

export type SaveMode = 'None' | 'PerRequest' | 'Grouped' | 'Both';
export type GroupedFormat = 'Json' | 'JsonLines';

export interface BatchSaveOptions {
  saveMode: SaveMode;
  groupedFormat: GroupedFormat;
  includeSummaryCsv: boolean;
}

export interface BatchPlanResult {
  totalRequests: number;
  exceedsHardLimit: boolean;
  validationError: string | null;
  warnings: string[];
}

export interface BatchPreviewItem {
  index: number;
  variablesSummary: string;
  method: HttpMethodType;
  finalUrl: string;
  headersText: string;
  hasHeaders: boolean;
  bodyText: string;
  hasBody: boolean;
  isInvalidJson: boolean;
  errorMessage?: string | null;
}

export interface BatchResultItem {
  index: number;
  paramsText: string;
  statusCode: number;
  statusCategory: StatusCategory;
  durationMs: number;
  sizeBytes: number;
  sizeText: string;
  contentType: string;
  requestUrl: string;
  varyingParams: KeyValueItem[];
  variableValues: KeyValueItem[];
  orderedAxisValues: KeyValueItem[];
  queryParams: KeyValueItem[];
  timestampUtc: string;
  errorMessage: string | null;
  response: ResponseModel | null;
  wasRateLimited: boolean;
  retryAttempts: number;
  finalWaitMs: number;
}

export type ChainExtractionSource = 'Body' | 'Header';
export type ExtractionKind = 'JsonPath' | 'Regex' | 'HtmlPath' | 'FullResponse';

export type ExtractionConditionMatchMode = 'All' | 'Any';

export type ExtractionConditionOperator =
  | 'Contains'
  | 'NotContains'
  | 'StartsWith'
  | 'NotStartsWith'
  | 'EndsWith'
  | 'NotEndsWith'
  | 'Equals'
  | 'NotEquals'
  | 'InList'
  | 'NotInList'
  | 'RegexMatch'
  | 'RegexNotMatch'
  | 'RegexExtract'
  | 'RegexExtractAll'
  | 'RegexReplace'
  | 'ReplaceText'
  | 'PrependText'
  | 'AppendText'
  | 'SetValue'
  | 'Between'
  | 'SplitPart'
  | 'Trim'
  | 'ToLower'
  | 'ToUpper'
  | 'IsEmpty'
  | 'NotEmpty'
  | 'LengthEquals'
  | 'LengthMin'
  | 'LengthMax'
  | 'NumberEquals'
  | 'NumberNotEquals'
  | 'GreaterThan'
  | 'GreaterOrEqual'
  | 'LessThan'
  | 'LessOrEqual';

export type ExtractionConditionalAction =
  | 'Keep'
  | 'Exclude'
  | 'SetValue'
  | 'PrependText'
  | 'AppendText'
  | 'ReplaceText'
  | 'RegexExtract'
  | 'RegexExtractAll'
  | 'RegexReplace'
  | 'Between'
  | 'SplitPart'
  | 'Trim'
  | 'ToLower'
  | 'ToUpper';

export interface ChainConditionalBranch {
  action: ExtractionConditionalAction;
  value: string;
  replacement: string;
  regexGroup: number;
  segmentIndex: number;
  ignoreCase: boolean;
}

export interface ChainExtractionCondition {
  id: string;
  isEnabled: boolean;
  isConditional: boolean;
  operator: ExtractionConditionOperator;
  value: string;
  replacement: string;
  regexGroup: number;
  segmentIndex: number;
  ignoreCase: boolean;
  thenBranch: ChainConditionalBranch;
  elseBranch: ChainConditionalBranch;
}

export interface ChainExtraction {
  id: string;
  variableName: string;
  source: ChainExtractionSource;
  kind: ExtractionKind;
  expression: string;
  regexGroup: number;
  headerName: string;
  isRequired: boolean;
  defaultValueIfMissing: string;
  isList: boolean;
  conditionMatchMode: ExtractionConditionMatchMode;
  deduplicateList: boolean;
  conditions: ChainExtractionCondition[];
  lastExtractedDisplay?: string | null;
  lastRawValues?: string[];
  lastErrorText?: string | null;
}

export interface ChainStep {
  id: string;
  name: string;
  curlInput: string;
  hasParsed: boolean;
  isCurlExpanded: boolean;
  method: HttpMethodType;
  url: string;
  varyingParams: VaryingParam[];
  headers: KeyValueItem[];
  bodyType: BodyType;
  body: string;
  formFields: KeyValueItem[];
  auth: AuthConfig;
  timeoutText: string;
  variables: BatchVariable[];
  axisOrderIds: string[];
  selectedMode: BatchMode;
  delayAfterMs: number;
  continueOnError: boolean;
  stepRateLimitUsesGlobalPolicy: boolean;
  extractions: ChainExtraction[];
  returnExpression: string;
  warnings: string[];
  lastResponse: ResponseModel | null;
}

export type ChainStepStatus = 'Success' | 'Failed' | 'Skipped';

export interface ChainStepResult {
  index: number;
  stepIndex: number;
  stepName: string;
  iteration: number;
  totalIterationsForStep: number;
  requestSummary: string;
  requestUrl: string;
  executedRequest: RequestModel | null;
  statusCode: number;
  statusCategory: StatusCategory;
  durationMs: number;
  sizeBytes: number;
  sizeText: string;
  contentType: string;
  response: ResponseModel | null;
  extractedValues: Record<string, string>;
  extractedLists: Record<string, string[]>;
  extractionErrors: string[];
  returnValue: string | null;
  isReturnOnly: boolean;
  status: ChainStepStatus;
  errorMessage: string | null;
  varyingParams: KeyValueItem[];
  variableValues: KeyValueItem[];
  orderedAxisValues: KeyValueItem[];
  queryParams: KeyValueItem[];
  timestampUtc: string;
  wasRateLimited: boolean;
  retryAttempts: number;
  finalWaitMs: number;
}

export interface ChainDeclaredVariableItem {
  name: string;
  sourceStepName: string;
  isList: boolean;
  isSensitive: boolean;
  itemCount: number;
  previewText: string;
  values: string[];
}

export type ChainSortMode = 'ChronologicalAsc' | 'ChronologicalDesc' | 'SizeAsc' | 'SizeDesc';
export type ChainCliLanguage = 'Python' | 'JavaScript' | 'Php';

export type ResponseFormat = 'Auto' | 'Json' | 'Xml' | 'Html' | 'Text';

export type ExtractorNodeKind =
  | 'JsonObject'
  | 'JsonArray'
  | 'JsonValue'
  | 'XmlElement'
  | 'XmlAttribute'
  | 'HtmlElement'
  | 'HtmlAttribute';

export type ExtractorValueKind =
  | 'Auto'
  | 'Digits'
  | 'NonWhitespace'
  | 'UntilEndOfLine'
  | 'Custom';

export type LiveTestStatus = 'None' | 'Success' | 'Warning' | 'Error';

export interface ExtractorNode {
  id: string;
  label: string;
  kindTag: string;
  nodeKind: ExtractorNodeKind;
  jsonPath: string | null;
  htmlPath: string | null;
  xpath: string | null;
  anchorName: string | null;
  elementTagName: string | null;
  attributeName: string | null;
  rawScalarValue: string | null;
  jsonKind?: 'string' | 'number' | 'boolean' | 'null' | 'object' | 'array';
  isLeaf: boolean;
  isSelectable: boolean;
  isExpanded: boolean;
  children: ExtractorNode[];
}

export interface ExtractionSuggestion {
  jsonPath: string | null;
  htmlPath: string | null;
  regex: string | null;
  occurrenceCount: number;
  occurrenceWarning: string | null;
  infoMessage: string | null;
}

export interface FormatAnalysisResult {
  detectedFormat: ResponseFormat;
  fallbackToText: boolean;
  statusMessage: string | null;
  rootNodes: ExtractorNode[];
}

export interface RealtimeExtractionSuggestion {
  expression: string;
  suggestedVariableName: string;
  previewValue: string;
  badgeText: string;
  detailText: string;
  suggestsList: boolean;
  matchCount: number;
  targetHeaderName: string | null;
}

export interface CookiePairItem {
  id: string;
  name: string;
  value: string;
  isEnabled: boolean;
}

export interface OverlayEditorRequest {
  title: string;
  text: string;
  isReadOnly: boolean;
  extraction?: ChainExtraction;
  rawExtractedValues?: string[];
  onSave?: (updatedText: string) => void;
  onSaveConditions?: (
    conditions: ChainExtractionCondition[],
    matchMode: ExtractionConditionMatchMode,
    deduplicate: boolean
  ) => void;
}

export interface CookieEditorRequest {
  headerName: string;
  rawHeaderValue: string;
  onSave: (newHeaderValue: string) => void;
}

let idCounter = 1;
export function createId(prefix = 'id'): string {
  idCounter += 1;
  return `${prefix}_${Date.now().toString(36)}_${idCounter.toString(36)}`;
}

export function createDefaultAuth(): AuthConfig {
  return {
    type: 'None',
    token: '',
    username: '',
    password: '',
    apiKeyName: '',
    apiKeyValue: '',
    apiKeyInHeader: true,
  };
}

export function createDefaultRateLimitPolicy(): RateLimitPolicy {
  return {
    isEnabled: true,
    maxRetriesPerRequest: 3,
    backoffInitialMs: 1000,
    backoffMaxMs: 30000,
    backoffMultiplier: 2,
    respectRetryAfter: true,
    maxRetryAfterSeconds: 60,
    extraRateLimitStatusCodes: '',
    pauseFloorMs: 0,
    quotaRemainingHeader: 'X-RateLimit-Remaining',
    quotaResetHeader: 'X-RateLimit-Reset',
    quotaThreshold: 2,
  };
}

export function createDefaultBatchVariable(name: string, locations: string[] = []): BatchVariable {
  return {
    id: createId('var'),
    name,
    locations,
    isUsed: locations.length > 0,
    isVarying: false,
    fixedValue: '',
    mode: 'Range',
    base: 10,
    startText: '1',
    endText: '10',
    stepText: '1',
    width: 0,
    uppercaseHex: true,
    usePrefix: false,
    listText: '',
    randomKind: 'Integer',
    randomCount: 10,
    isUnique: false,
    seedText: '',
    autoSeed: Math.floor(Math.random() * 900000) + 100000,
    minText: '1',
    maxText: '100',
    byteCount: 8,
    textLength: 10,
    includeLowercase: true,
    includeUppercase: true,
    includeDigits: true,
    includeDashUnderscore: false,
  };
}

export function createDefaultBranch(action: ExtractionConditionalAction = 'Keep'): ChainConditionalBranch {
  return {
    action,
    value: '',
    replacement: '',
    regexGroup: 1,
    segmentIndex: 0,
    ignoreCase: false,
  };
}

export function createDefaultCondition(isConditional = false): ChainExtractionCondition {
  return {
    id: createId('cond'),
    isEnabled: true,
    isConditional,
    operator: isConditional ? 'Equals' : 'Contains',
    value: '',
    replacement: '',
    regexGroup: 1,
    segmentIndex: 0,
    ignoreCase: false,
    thenBranch: createDefaultBranch('Keep'),
    elseBranch: createDefaultBranch('Exclude'),
  };
}

export function createDefaultExtraction(): ChainExtraction {
  return {
    id: createId('ext'),
    variableName: '',
    source: 'Body',
    kind: 'JsonPath',
    expression: '',
    regexGroup: 1,
    headerName: '',
    isRequired: true,
    defaultValueIfMissing: '',
    isList: false,
    conditionMatchMode: 'All',
    deduplicateList: false,
    conditions: [],
    lastExtractedDisplay: null,
    lastRawValues: [],
    lastErrorText: null,
  };
}

export function createDefaultChainStep(rank: number): ChainStep {
  return {
    id: createId('step'),
    name: `Étape ${rank}`,
    curlInput: '',
    hasParsed: false,
    isCurlExpanded: true,
    method: 'GET',
    url: '',
    varyingParams: [],
    headers: [],
    bodyType: 'None',
    body: '',
    formFields: [],
    auth: createDefaultAuth(),
    timeoutText: '30',
    variables: [],
    axisOrderIds: [],
    selectedMode: 'Combinations',
    delayAfterMs: 300,
    continueOnError: false,
    stepRateLimitUsesGlobalPolicy: true,
    extractions: [],
    returnExpression: '',
    warnings: [],
    lastResponse: null,
  };
}
