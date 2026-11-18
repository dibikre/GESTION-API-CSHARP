namespace ApiTester.Models;

public enum HttpMethodType
{
    Get,
    Post,
    Put,
    Patch,
    Delete
}

public enum BodyType
{
    None,
    Json,
    Text,
    FormUrlEncoded
}

public enum AuthType
{
    None,
    Bearer,
    Basic,
    ApiKey
}
