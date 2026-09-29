namespace ClaudeUsageWidget.Core;

public static class ConnectionFailure
{
    public static string Describe(string kind, string stage, int status)
    {
        if (kind == "wrong_origin") return "일반 브라우저에서 Claude 로그인을 완료해 주세요.";
        if (kind == "unexpected_shape") return "계정 응답 형식이 달라 연결하지 못했습니다. 로그인 실패로 확인된 것은 아닙니다.";
        if (kind == "no_organizations") return "로그인 계정에 사용 가능한 조직이 없습니다. 브라우저의 Claude 사용량 화면을 확인해 주세요.";
        if (kind == "choose_organization") return "브라우저에서 사용할 Claude 조직을 선택하고 사용량 화면을 열어 주세요.";
        if (kind == "invalid_organization") return "계정의 조직 정보를 읽지 못했습니다. 브라우저에서 사용량 화면을 열어 주세요.";
        if (kind == "invalid_json") return "서버가 사용량 데이터 대신 다른 응답을 보냈습니다. 브라우저의 보안 확인이나 오류 안내를 확인해 주세요.";
        if (status == 401) return stage == "usage" ? "사용량 조회가 인증되지 않았습니다. 브라우저에서 Claude 사용량 화면을 확인해 주세요."
            : "브라우저에서 로그인을 완료하면 자동으로 연결됩니다.";
        if (status == 403) return "조회가 거부되었습니다. 브라우저에서 보안 확인 또는 접근 권한 안내를 확인해 주세요.";
        if (status == 429) return "요청이 많아 잠시 대기합니다. 자동으로 다시 조회합니다.";
        return "사용량을 가져오지 못했습니다. 네트워크와 브라우저의 Claude 사용량 화면을 확인해 주세요.";
    }
}
