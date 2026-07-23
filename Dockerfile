# ใช้ ASP.NET Core Runtime เป็น base image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

# ใช้ .NET SDK image สำหรับการ Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# ติดตั้ง Node.js เพื่อใช้ Build Tailwind CSS (ถ้ามี)
RUN apt-get update && apt-get install -y curl \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs

# คัดลอกไฟล์ที่จำเป็นสำหรับการ restore
COPY ["CostFlow.csproj", "./"]
COPY ["package.json", "./"]
COPY ["package-lock.json", "./"]

# Restore dependencies ของทั้ง .NET และ Node.js
RUN dotnet restore "./CostFlow.csproj"
RUN npm ci

# คัดลอก Source Code ทั้งหมด
COPY . .

# Build CSS ด้วย Tailwind
RUN npm run build:css

# Build และ Publish โปรเจ็กต์ .NET
RUN dotnet publish "CostFlow.csproj" -c Release -o /app/publish /p:UseAppHost=false

# นำไฟล์ที่ Publish แล้วมาใส่ใน Base image เพื่อรัน
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CostFlow.dll"]
