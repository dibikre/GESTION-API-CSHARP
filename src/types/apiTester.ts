export type HttpMethodType = 'Get' | 'Post' | 'Put' | 'Delete' | 'Patch' | 'Head' | 'Options';

export type BodyType = 'None' | 'Json' | 'Text' | 'FormUrlEncoded';

export type AuthType = 'None' | 'Bearer' | 'Basic' | 'ApiKey';

export type BatchMode = 'Combinations' | 'Parallel';

export type VariableMode = 'Range' | 'List' | 'Random';

export type NumericBase = 2 | 8 | 10 | 16;

export type RandomValueKind = 'Integer' | 'HexBytes' | 'Text' | 'Guid' | 'PickFromList';

export type SaveMode = 'None' | 'PerRequest' | 'Grouped' | 'Both';

export type GroupedFormat = 'Json' | 'JsonLines';

export type StatusCategory = 'None' | 'Success' | 'Redirect' | 'ClientError' | 'ServerError' | 'NetworkError';

export type BatchAxisKind = 'UrlParam' | 'Variable' | 'ExtractedList';

export type ExtractionKind = 'JsonPath' | 'Regex' | 'FullResponse';

export type ResponseFilterMode = 'Auto' | 'JsonPath' | 'Regex' | 'XPath';

export type ChainExtractionSource = 'Body' | 'Header' | 'Variable';

export type ChainStepStatus = 'Success' | 'Failed' | 'Skipped';

export type ChainRunStatus = 'Success' | 'Failed' | 'Cancelled';

export type ChainCliLanguage = 'Python' | 'JavaScript' | 'Php';

export type ChainResultSortMode = 'ChronologicalAsc' | 'ChronologicalDesc' | 'SizeDesc' | 'SizeAsc';

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

export interface KeyValueItem {
  id: string;
  key: string;
  value: string;
  isEnabled: boolean;
}

export interface CookiePairItem {
  id: string;
  name: string;
  value: string;
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
  errorMessage?: string | null;
}

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

export interface BatchVariable {
  id: string;
  name: string;
  usedIn: string;
  isUsed: boolean;
  isVarying: boolean;
  fixedValue: string;
  mode: VariableMode;
  base: NumericBase;
  startText: string;
  endText: string;
  stepText: string;
  width: number;
  uppercaseHex: boolean;
  usePrefix: boolean;
  listText: string;
  randomKind: RandomValueKind;
  randomCount: number;
  isUnique: boolean;
  seedText: string;
  seedUsed: number | null;
  minText: string;
  maxText: string;
  byteCount: number;
  textLength: number;
  includeLowercase: boolean;
  includeUppercase: boolean;
  includeDigits: boolean;
  includeDashUnderscore: boolean;
  isConfigured: boolean;
  randomValues: string[];
}

export interface BatchAxis {
  id: string;
  kind: BatchAxisKind;
  name: string;
  isRange: boolean;
  start: bigint;
  end: bigint;
  step: bigint;
  base: NumericBase;
  width: number;
  uppercaseHex: boolean;
  usePrefix: boolean;
  values: string[];
  valueCount: number;
  sourceParamId?: string;
  sourceVariableId?: string;
}

export interface RateLimitPolicy {
  isEnabled: boolean;
  respectRetryAfter: boolean;
  maxRetryAfterSeconds: number;
  enableBackoff: boolean;
  backoffInitialMs: number;
  backoffMultiplier: number;
  backoffMaxMs: number;
  maxRetriesPerRequest: number;
  extraStatusCodes: number[];
  quotaRemainingHeader: string;
  quotaResetHeader: string;
  quotaThreshold: number;
  pauseFloorMs: number;
}

export interface BatchSaveOptions {
  saveMode: SaveMode;
  outputFolder: string;
  groupedFormat: GroupedFormat;
  createTimestampSubfolder: boolean;
  includeSummaryCsv: boolean;
}

export interface BatchResultItem {
  index: number;
  paramsText: string;
  requestUrl: string;
  statusCode: number;
  statusCategory: StatusCategory;
  durationMs: number;
  sizeBytes: number;
  sizeText: string;
  contentType: string;
  errorMessage: string | null;
  response: ResponseModel | null;
  executedRequest: RequestModel | null;
  varyingParams: KeyValueItem[];
  variableValues: KeyValueItem[];
  orderedAxisValues: KeyValueItem[];
  queryParams: KeyValueItem[];
  timestampUtc: string;
  wasRateLimited: boolean;
  retryAttempts: number;
  finalWaitMs: number;
  stepIndex?: number;
  stepName?: string;
  iteration?: number;
  extractedValues?: Record<string, string>;
}

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
  kind: ExtractionKind;
  expression: string;
  regexGroup: number;
  source: ChainExtractionSource;
  headerName: string;
  sourceVariableName: string;
  isRequired: boolean;
  isList: boolean;
  defaultValueIfMissing: string;
  conditionMatchMode: ExtractionConditionMatchMode;
  deduplicateList: boolean;
  conditions: ChainExtractionCondition[];
  testResultText?: string;
  testSucceeded?: boolean;
  hasTestResult?: boolean;
}

export interface ChainStep {
  id: string;
  name: string;
  curlText: string;
  hasParsed: boolean;
  method: HttpMethodType;
  url: string;
  body: string;
  bodyType: BodyType;
  returnExpression: string;
  selectedMode: BatchMode;
  delayAfterMs: number;
  continueOnError: boolean;
  stepRateLimitUsesGlobalPolicy: boolean;
  parsedTimeoutSeconds: number;
  parsedAuth: AuthConfig;
  parseWarnings: string[];
  headers: KeyValueItem[];
  formFields: KeyValueItem[];
  varyingParams: VaryingParam[];
  variables: BatchVariable[];
  axisOrder: Array<{ kind: BatchAxisKind; name: string }>;
  extractions: ChainExtraction[];
}

export interface ChainStepResult extends BatchResultItem {
  stepIndex: number;
  stepName: string;
  iteration: number;
  totalIterationsForStep: number;
  requestSummary: string;
  extractedValues: Record<string, string>;
  extractedLists: Record<string, string[]>;
  extractionErrors: string[];
  returnValue: string | null;
  isReturnOnly: boolean;
  status: ChainStepStatus;
}

export interface DeclaredVariableInfo {
  name: string;
  isList: boolean;
  valuePreview: string;
  sourceStepIndex: number;
  sourceStepName: string;
  sourceKind: 'Extracted' | 'LocalFixed' | 'LocalVarying';
  itemCount?: number;
  values?: string[];
  isSensitive?: boolean;
}

export interface ExtractionNodeItem {
  id: string;
  kind: 'Json' | 'Html' | 'Xml';
  key: string;
  valuePreview: string;
  rawValue: string;
  pathExpression: string;
  wildcardPathExpression?: string;
  children: ExtractionNodeItem[];
}

export interface OverlayEditorTextRequest {
  kind: 'text';
  title: string;
  subtitle?: string;
  text: string;
  isReadOnly?: boolean;
  allowJsonFormat?: boolean;
  onSave?: (text: string) => void;
}

export interface OverlayEditorConditionsRequest {
  kind: 'conditions';
  title: string;
  subtitle?: string;
  variableName: string;
  rawItems: string[];
  initialConditions: ChainExtractionCondition[];
  initialMatchMode: ExtractionConditionMatchMode;
  initialDeduplicate: boolean;
  onApply: (
    conditions: ChainExtractionCondition[],
    matchMode: ExtractionConditionMatchMode,
    deduplicate: boolean,
  ) => void;
}

export type OverlayEditorRequest = OverlayEditorTextRequest | OverlayEditorConditionsRequest;

export interface CookieEditorRequest {
  title: string;
  subtitle?: string;
  initialCookieHeader: string;
  onSave: (headerValue: string) => void;
}

// ============================================================================
// Constants & Label Helpers (from ViewValueConverters.cs)
// ============================================================================

export const HTTP_METHODS: HttpMethodType[] = ['Get', 'Post', 'Put', 'Delete', 'Patch', 'Head', 'Options'];

export const HTTP_METHOD_LABELS: Record<HttpMethodType, string> = {
  Get: 'GET',
  Post: 'POST',
  Put: 'PUT',
  Delete: 'DELETE',
  Patch: 'PATCH',
  Head: 'HEAD',
  Options: 'OPTIONS',
};

export const BODY_TYPES: BodyType[] = ['None', 'Json', 'Text', 'FormUrlEncoded'];

export const BODY_TYPE_LABELS: Record<BodyType, string> = {
  None: 'Aucun',
  Json: 'JSON',
  Text: 'Texte brut',
  FormUrlEncoded: 'Formulaire (x-www-form-urlencoded)',
};

export const AUTH_TYPES: AuthType[] = ['None', 'Bearer', 'Basic', 'ApiKey'];

export const AUTH_TYPE_LABELS: Record<AuthType, string> = {
  None: 'Aucune',
  Bearer: 'Bearer Token',
  Basic: 'Basic Auth',
  ApiKey: 'API Key',
};

export const BATCH_MODES: BatchMode[] = ['Combinations', 'Parallel'];

export const BATCH_MODE_LABELS: Record<BatchMode, string> = {
  Combinations: 'Combinaisons',
  Parallel: 'En parallèle',
};

export const VARIABLE_MODES: VariableMode[] = ['Range', 'List', 'Random'];

export const VARIABLE_MODE_LABELS: Record<VariableMode, string> = {
  Range: 'Plage (incrément)',
  List: 'Liste de valeurs',
  Random: 'Aléatoire',
};

export const NUMERIC_BASES: NumericBase[] = [2, 8, 10, 16];

export const NUMERIC_BASE_LABELS: Record<NumericBase, string> = {
  2: 'Binaire (base 2)',
  8: 'Octal (base 8)',
  10: 'Décimal (base 10)',
  16: 'Hexadécimal (base 16)',
};

export const RANDOM_KINDS: RandomValueKind[] = ['Integer', 'HexBytes', 'Text', 'Guid', 'PickFromList'];

export const RANDOM_KIND_LABELS: Record<RandomValueKind, string> = {
  Integer: 'Entier',
  HexBytes: 'Hexadécimal (octets)',
  Text: 'Texte',
  Guid: 'GUID',
  PickFromList: 'Choix dans une liste',
};

export const SAVE_MODES: SaveMode[] = ['None', 'PerRequest', 'Grouped', 'Both'];

export const SAVE_MODE_LABELS: Record<SaveMode, string> = {
  None: 'Ne pas enregistrer',
  PerRequest: 'Un fichier par requête',
  Grouped: 'Un seul fichier groupé',
  Both: 'Les deux',
};

export const GROUPED_FORMATS: GroupedFormat[] = ['Json', 'JsonLines'];

export const GROUPED_FORMAT_LABELS: Record<GroupedFormat, string> = {
  Json: 'JSON (un tableau)',
  JsonLines: 'JSON Lines (une ligne par requête)',
};

export const EXTRACTION_SOURCES: ChainExtractionSource[] = ['Body', 'Header', 'Variable'];

export const EXTRACTION_SOURCE_LABELS: Record<ChainExtractionSource, string> = {
  Body: 'Corps',
  Header: 'En-tête',
  Variable: 'Variable',
};

export const EXTRACTION_KINDS: ExtractionKind[] = ['JsonPath', 'Regex', 'FullResponse'];

export const EXTRACTION_KIND_LABELS: Record<ExtractionKind, string> = {
  JsonPath: 'JSONPath',
  Regex: 'Regex',
  FullResponse: 'Réponse complète',
};

export const RESPONSE_FILTER_MODES: ResponseFilterMode[] = ['Auto', 'JsonPath', 'Regex', 'XPath'];

export const RESPONSE_FILTER_MODE_LABELS: Record<ResponseFilterMode, string> = {
  Auto: 'Auto',
  JsonPath: 'JSONPath',
  Regex: 'Regex',
  XPath: 'XPath',
};

export const CHAIN_CLI_LANGUAGES: ChainCliLanguage[] = ['Python', 'JavaScript', 'Php'];

export const CHAIN_CLI_LANGUAGE_LABELS: Record<ChainCliLanguage, string> = {
  Python: 'Python',
  JavaScript: 'JavaScript',
  Php: 'PHP',
};

export const CHAIN_RESULT_SORT_MODES: ChainResultSortMode[] = [
  'ChronologicalAsc',
  'ChronologicalDesc',
  'SizeDesc',
  'SizeAsc',
];

export const CHAIN_RESULT_SORT_LABELS: Record<ChainResultSortMode, string> = {
  ChronologicalAsc: 'Chronologique (ancien)',
  ChronologicalDesc: 'Chronologique (récent)',
  SizeDesc: 'Taille (décroissante)',
  SizeAsc: 'Taille (croissante)',
};

export const CONDITION_MATCH_MODE_LABELS: Record<ExtractionConditionMatchMode, string> = {
  All: 'Toutes (ET)',
  Any: 'Au moins une (OU)',
};

export const CONDITION_OPERATORS: ExtractionConditionOperator[] = [
  'Contains',
  'NotContains',
  'StartsWith',
  'NotStartsWith',
  'EndsWith',
  'NotEndsWith',
  'Equals',
  'NotEquals',
  'InList',
  'NotInList',
  'RegexMatch',
  'RegexNotMatch',
  'RegexExtract',
  'RegexExtractAll',
  'RegexReplace',
  'ReplaceText',
  'PrependText',
  'AppendText',
  'SetValue',
  'Between',
  'SplitPart',
  'Trim',
  'ToLower',
  'ToUpper',
  'IsEmpty',
  'NotEmpty',
  'LengthEquals',
  'LengthMin',
  'LengthMax',
  'NumberEquals',
  'NumberNotEquals',
  'GreaterThan',
  'GreaterOrEqual',
  'LessThan',
  'LessOrEqual',
];

export const FILTER_CONDITION_OPERATORS: ExtractionConditionOperator[] = CONDITION_OPERATORS.filter(isFilterOperator);

export const CONDITION_OPERATOR_LABELS: Record<ExtractionConditionOperator, string> = {
  Contains: 'Contient',
  NotContains: 'Ne contient pas',
  StartsWith: 'Commence par',
  NotStartsWith: 'Ne commence pas par',
  EndsWith: 'Finit par',
  NotEndsWith: 'Ne finit pas par',
  Equals: 'Égal à',
  NotEquals: 'Différent de',
  InList: 'Dans la liste (a, b, c)',
  NotInList: 'Pas dans la liste (a, b, c)',
  RegexMatch: 'Correspond à Regex',
  RegexNotMatch: 'Ne correspond pas à Regex',
  RegexExtract: 'Extraire via Regex',
  RegexExtractAll: 'Extraire tout via Regex',
  RegexReplace: 'Remplacer via Regex',
  ReplaceText: 'Remplacer texte',
  PrependText: 'Préfixer par',
  AppendText: 'Suffixer par',
  SetValue: 'Remplacer par valeur fixe',
  Between: 'Extraire entre (début -> fin)',
  SplitPart: 'Découper (séparateur + index)',
  Trim: 'Nettoyer bords (Trim)',
  ToLower: 'En minuscules',
  ToUpper: 'En majuscules',
  IsEmpty: 'Est vide',
  NotEmpty: 'Non vide',
  LengthEquals: 'Longueur égale à',
  LengthMin: 'Longueur min (>=)',
  LengthMax: 'Longueur max (<=)',
  NumberEquals: 'Nombre égal à (==)',
  NumberNotEquals: 'Nombre différent de (!=)',
  GreaterThan: 'Nombre >',
  GreaterOrEqual: 'Nombre >=',
  LessThan: 'Nombre <',
  LessOrEqual: 'Nombre <=',
};

export const CONDITIONAL_ACTIONS: ExtractionConditionalAction[] = [
  'Keep',
  'Exclude',
  'SetValue',
  'PrependText',
  'AppendText',
  'ReplaceText',
  'RegexExtract',
  'RegexExtractAll',
  'RegexReplace',
  'Between',
  'SplitPart',
  'Trim',
  'ToLower',
  'ToUpper',
];

export const CONDITIONAL_ACTION_LABELS: Record<ExtractionConditionalAction, string> = {
  Keep: 'Garder tel quel',
  Exclude: 'Exclure (supprimer)',
  SetValue: 'Définir valeur fixe',
  PrependText: 'Préfixer par',
  AppendText: 'Suffixer par',
  ReplaceText: 'Remplacer texte',
  RegexExtract: 'Extraire via Regex',
  RegexExtractAll: 'Extraire tout via Regex',
  RegexReplace: 'Remplacer via Regex',
  Between: 'Extraire entre',
  SplitPart: 'Découper (séparateur + index)',
  Trim: 'Nettoyer bords (Trim)',
  ToLower: 'En minuscules',
  ToUpper: 'En majuscules',
};

export function isFilterOperator(op: ExtractionConditionOperator): boolean {
  return ![
    'RegexExtract',
    'RegexExtractAll',
    'RegexReplace',
    'ReplaceText',
    'PrependText',
    'AppendText',
    'SetValue',
    'Between',
    'SplitPart',
    'Trim',
    'ToLower',
    'ToUpper',
  ].includes(op);
}

export function operatorRequiresValue(op: ExtractionConditionOperator): boolean {
  return !['IsEmpty', 'NotEmpty', 'ToLower', 'ToUpper'].includes(op);
}

export function operatorSupportsReplacement(op: ExtractionConditionOperator): boolean {
  return op === 'RegexReplace' || op === 'ReplaceText' || op === 'Between';
}

export function operatorSupportsRegexGroup(op: ExtractionConditionOperator): boolean {
  return op === 'RegexExtract' || op === 'RegexExtractAll';
}

export function operatorSupportsSegmentIndex(op: ExtractionConditionOperator): boolean {
  return op === 'SplitPart';
}

export function operatorSupportsIgnoreCase(op: ExtractionConditionOperator): boolean {
  return [
    'Contains',
    'NotContains',
    'StartsWith',
    'NotStartsWith',
    'EndsWith',
    'NotEndsWith',
    'Equals',
    'NotEquals',
    'InList',
    'NotInList',
    'RegexMatch',
    'RegexNotMatch',
    'RegexExtract',
    'RegexExtractAll',
    'RegexReplace',
    'ReplaceText',
    'Between',
    'SplitPart',
  ].includes(op);
}

export function branchRequiresValue(action: ExtractionConditionalAction): boolean {
  return !['Keep', 'Exclude', 'ToLower', 'ToUpper'].includes(action);
}

export function branchSupportsReplacement(action: ExtractionConditionalAction): boolean {
  return action === 'RegexReplace' || action === 'ReplaceText' || action === 'Between';
}

export function branchSupportsRegexGroup(action: ExtractionConditionalAction): boolean {
  return action === 'RegexExtract' || action === 'RegexExtractAll';
}

export function branchSupportsSegmentIndex(action: ExtractionConditionalAction): boolean {
  return action === 'SplitPart';
}

export function branchSupportsIgnoreCase(action: ExtractionConditionalAction): boolean {
  return [
    'ReplaceText',
    'RegexExtract',
    'RegexExtractAll',
    'RegexReplace',
    'Between',
    'SplitPart',
  ].includes(action);
}

export function createId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`;
}

export function createDefaultAuth(): AuthConfig {
  return {
    type: 'None',
    token: '',
    username: '',
    password: '',
    apiKeyName: 'X-API-Key',
    apiKeyValue: '',
    apiKeyInHeader: true,
  };
}

export function createDefaultRateLimitPolicy(): RateLimitPolicy {
  return {
    isEnabled: true,
    respectRetryAfter: true,
    maxRetryAfterSeconds: 120,
    enableBackoff: false,
    backoffInitialMs: 500,
    backoffMultiplier: 2.0,
    backoffMaxMs: 30000,
    maxRetriesPerRequest: 3,
    extraStatusCodes: [],
    quotaRemainingHeader: '',
    quotaResetHeader: '',
    quotaThreshold: 1,
    pauseFloorMs: 300,
  };
}

export function createDefaultSaveOptions(): BatchSaveOptions {
  return {
    saveMode: 'None',
    outputFolder: 'lot_exports',
    groupedFormat: 'Json',
    createTimestampSubfolder: true,
    includeSummaryCsv: true,
  };
}

export function createDefaultCondition(): ChainExtractionCondition {
  return {
    id: createId(),
    isEnabled: true,
    isConditional: false,
    operator: 'Contains',
    value: '',
    replacement: '',
    regexGroup: 1,
    segmentIndex: 0,
    ignoreCase: false,
    thenBranch: {
      action: 'Keep',
      value: '',
      replacement: '',
      regexGroup: 1,
      segmentIndex: 0,
      ignoreCase: false,
    },
    elseBranch: {
      action: 'Keep',
      value: '',
      replacement: '',
      regexGroup: 1,
      segmentIndex: 0,
      ignoreCase: false,
    },
  };
}

export function createDefaultExtraction(): ChainExtraction {
  return {
    id: createId(),
    variableName: '',
    kind: 'JsonPath',
    expression: '',
    regexGroup: 1,
    source: 'Body',
    headerName: '',
    sourceVariableName: '',
    isRequired: true,
    isList: false,
    defaultValueIfMissing: '',
    conditionMatchMode: 'All',
    deduplicateList: false,
    conditions: [],
  };
}

export function createDefaultRequest(): RequestModel {
  return {
    method: 'Get',
    url: '',
    queryParams: [],
    headers: [],
    bodyType: 'None',
    body: '',
    formFields: [],
    auth: createDefaultAuth(),
    timeoutSeconds: 30,
  };
}
