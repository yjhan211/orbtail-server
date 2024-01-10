# .NET SDK 이미지를 사용하여 애플리케이션 빌드
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 프로젝트 파일 복사
COPY ["server/server.csproj", "server/"]
COPY ["network/network.csproj", "network/"]

# NuGet 패키지 복원
RUN dotnet restore "server/server.csproj"

# 소스 코드 복사
COPY . .

# 빌드
WORKDIR "/src/server"
RUN dotnet build "server.csproj" -c Release -o /app/build

# 실행 파일 생성
FROM build AS publish
RUN dotnet publish "server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# .NET Runtime 이미지를 기반으로 최종 이미지 생성
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# 빌드된 실행 파일 복사
COPY --from=publish /app/publish .

# 애플리케이션 실행 명령
ENTRYPOINT ["dotnet", "server.dll"]
